using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace overloadstop_calibrator
{
    public partial class MainWindow : Window
    {
        private static readonly Regex _numberRegex = new Regex(@"[-+]?\d*\.?\d+", RegexOptions.Compiled);
        private static readonly int[] _commonBaudRates = new int[] { 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200 };

        // Adjust to match your LDU's command protocol and line termination
        private readonly string _pollCommand = "GG\r";
        private readonly string _responseNewLine = "\r";
        private readonly int _pollIntervalMs = 80;

        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private SerialPort _serialPort;
        private Thread _workerThread;
        private volatile bool _isConnected;
        private volatile string _latestFormattedReading = "0.00000 mV";
        private volatile bool _uiUpdatePending;

        public MainWindow()
        {
            InitializeComponent();
            InitializeControls();
        }

        private void InitializeControls()
        {
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports);
            cmbPort.ItemsSource = ports;
            if (ports.Length > 0)
            {
                cmbPort.SelectedIndex = 0;
            }

            cmbBaudRate.ItemsSource = _commonBaudRates;
            cmbBaudRate.SelectedItem = 9600;
            lblDisplay.Content = _latestFormattedReading;
        }

        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                Disconnect();
                return;
            }

            if (cmbPort.SelectedItem == null || cmbBaudRate.SelectedItem == null)
            {
                MessageBox.Show("Select a COM port and baud rate.", "Serial Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string portName = cmbPort.SelectedItem.ToString();
                int baudRate = (int)cmbBaudRate.SelectedItem;

                _stopEvent.Reset();
                _uiUpdatePending = false;

                SerialPort port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
                port.NewLine = _responseNewLine;
                port.ReadTimeout = 150;
                port.WriteTimeout = 200;
                port.DtrEnable = true;
                port.RtsEnable = true;
                port.Open();
                port.DiscardInBuffer();

                _serialPort = port;
                _isConnected = true;

                btnConnect.Content = "Disconnect";
                cmbPort.IsEnabled = false;
                cmbBaudRate.IsEnabled = false;

                _workerThread = new Thread(PollLduWorker);
                _workerThread.IsBackground = true;
                _workerThread.Name = "LduSerialWorker";
                _workerThread.Start();
            }
            catch (Exception ex)
            {
                Disconnect();
                MessageBox.Show("Unable to connect: " + ex.Message, "Serial Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PollLduWorker()
        {
            Console.WriteLine("LDU Polling Worker Thread Started");
            while (_isConnected && !_stopEvent.WaitOne(0))
            {
                Console.WriteLine("Polling LDU...");
                SerialPort port = _serialPort;
                if (port == null || !port.IsOpen)
                {
                    break;
                }

                try
                {
                    port.DiscardInBuffer();
                    port.Write(_pollCommand);

                    Thread.Sleep(20); // Allow time for the LDU to respond

                    Console.WriteLine("Write "  + _pollCommand );

                    string line = port.ReadExisting();

                    Console.WriteLine("Read " + line);

                    if (!string.IsNullOrEmpty(line))
                    {
                        string formatted = FormatMillivolts(line);
                        PublishReading(formatted);
                    }
                }
                catch (TimeoutException)
                {
                    // Normal when a single poll cycle has no frame within ReadTimeout
                }
                catch (Exception)
                {
                    if (!_isConnected || _stopEvent.WaitOne(0))
                    {
                        break;
                    }
                }

                if (_stopEvent.WaitOne(_pollIntervalMs))
                {
                    break;
                }
            }
        }

        private void PublishReading(string formattedReading)
        {
            _latestFormattedReading = formattedReading;

            if (!_uiUpdatePending)
            {
                _uiUpdatePending = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(FlushReadingToUi));
            }
        }

        private void FlushReadingToUi()
        {
            _uiUpdatePending = false;
            lblDisplay.Content = _latestFormattedReading;
        }

        private static string FormatMillivolts(string raw)
        {
            string trimmed = raw.Trim();
            Match match = _numberRegex.Match(trimmed);

            double value;
            if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return value.ToString("0.00000", CultureInfo.InvariantCulture) + " mV";
            }

            return trimmed;
        }

        private void Disconnect()
        {
            _isConnected = false;
            _stopEvent.Set();

            Thread worker = _workerThread;
            _workerThread = null;
            if (worker != null && worker.IsAlive)
            {
                worker.Join(1000);
            }

            SerialPort port = _serialPort;
            _serialPort = null;
            if (port != null)
            {
                if (port.IsOpen)
                {
                    try
                    {
                        port.Close();
                    }
                    catch (Exception)
                    {
                        // Ignore driver close errors during teardown
                    }
                }
                port.Dispose();
            }

            _uiUpdatePending = false;
            btnConnect.Content = "Connect";
            cmbPort.IsEnabled = true;
            cmbBaudRate.IsEnabled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            _stopEvent.Close();
            base.OnClosed(e);
        }
    }
}