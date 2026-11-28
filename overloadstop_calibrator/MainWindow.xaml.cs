using System;
using System.Data;
using System.Globalization;
using System.IO.Ports;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace overloadstop_calibrator
{
    public partial class MainWindow : Window
    {
        private readonly DatabaseConnection _db = new DatabaseConnection();

        // WIP table that holds the serial numbers and their S3 part numbers
        private const string _wipTable = "tsfs.tblfwipserial";

        // True only after a serial has been looked up and its profile loaded
        private bool _profileLoaded;

        // Values captured on the UI thread when Start is pressed, used when saving
        private string _serialNumber = string.Empty;
        private string _operatorIdText = string.Empty;
        private string _profileId = string.Empty;
        private readonly StringBuilder _scanBuffer = new StringBuilder();
        private DispatcherTimer _scanIdleTimer;
        private bool _loginInProgress;

        private static readonly Regex _numberRegex = new Regex(@"[-+]?\d*\.?\d+", RegexOptions.Compiled);
        private static readonly int[] _commonBaudRates = new int[] { 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200 };

        // Name of the applied load shown in instructions (read from txtApplyLoad on Start)
        private string _loadLabel = "the load";

        // Countdown panel blink
        private DispatcherTimer _blinkTimer;
        private bool _blinkOn;
        private static readonly Color _blinkIdle = (Color)ColorConverter.ConvertFromString("#EEF2F6");
        private static readonly Color _blinkAlert = (Color)ColorConverter.ConvertFromString("#FCD34D");

        // Adjust to match your LDU's command protocol and line termination
        private readonly string _pollCommand = "GN\r";
        private readonly string _tareCommand = "ST\r";
        private readonly string _resetCommand = "RT\r";
        private readonly string _responseNewLine = "\r";
        private readonly int _pollIntervalMs = 80;

        // Text the LDU must return after RT for the connection to be considered successful
        private const string _resetSuccessToken = "OK";
        private readonly int _resetResponseWaitMs = 200;

        // Stability & calibration tuning parameters
        private readonly double _stabilityTolerance = 0.00003;
        private readonly double _zeroTolerance = 0.00005;      // reading must display as 0.00000
        private readonly double _tareZeroTolerance = 0.00005;   // reading must be within this of zero after tare
        private readonly int _requiredStableSamples = 3;
        private readonly int _stabilityTimeoutMs = 5000;
        private readonly int _maxTareAttempts = 5;
        private readonly int _tareCountdownSeconds = 2;
        private readonly int _screwHoldMs = 10000;
        private readonly int _screwPostWaitMs = 0;

        // mV change that counts as load applied (tune for your setup)
        private readonly double _loadDetectThreshold = 0.00100;

        // mV drop after the screw step that counts as the load being removed
        private readonly double _unloadDropThreshold = 0.00500;

        private double _screwMin;
        private double _screwMax;
        private double _resultMin;
        private double _resultMax;

        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _autoTareStopEvent = new ManualResetEvent(true);
        private readonly object _portWriteLock = new object();
        private readonly object _readingLock = new object();

        private SerialPort _serialPort;
        private Thread _workerThread;
        private Thread _autoTareThread;
        private volatile bool _isConnected;
        private volatile bool _isAutoTareRunning;
        private volatile bool _pollingPaused;
        private volatile string _latestFormattedReading = "0.00000 mV/V";
        private volatile bool _uiUpdatePending;

        private double _latestNumericValue;
        private bool _hasNumericValue;
        private long _readingSequence;

        private Brush _defaultDisplayBrush;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private OperatorInfo _currentOperator;

        public MainWindow()
        {
            InitializeComponent();
            InitializeControls();
            InitializeLoginScanner();
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
            lblDisplay.Content = BuildReadingText(_latestFormattedReading);
            lblTareCountDown.Content = _tareCountdownSeconds.ToString(CultureInfo.InvariantCulture);
            lblStatus.Content = "--";
            lblR1.Content = "--";
            lblR2.Content = "--";
            lblResult.Content = "--";
            _defaultDisplayBrush = lblDisplay.Foreground;

            // Editing the serial clears the loaded profile until it is looked up again
            txtSerial.TextChanged += (s, e) =>
            {
                _profileLoaded = false;
                SetStartEnabled(true);
            };

            SetStartEnabled(false);
            SetCommandButtonsEnabled(false);
        }

        private void InitializeLoginScanner()
        {
            // If a scan is cut off partway (no Enter arrives), discard the partial input.
            _scanIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _scanIdleTimer.Tick += (s, e) =>
            {
                _scanIdleTimer.Stop();
                _scanBuffer.Clear();
            };

            PreviewTextInput += OnLoginTextInput;
            PreviewKeyDown += OnLoginKeyDown;
            Loaded += (s, e) => FocusLoginScanner();
        }

        private bool IsLoginVisible
        {
            get { return gridLogin.Visibility == Visibility.Visible; }
        }

        private void FocusLoginScanner()
        {
            if (IsLoginVisible)
            {
                Focus();
            }
        }

        // Scanner keystrokes arrive here. Only handled while the login screen is showing,
        // so typing in Serial No. or the Data tab is not affected.
        private void OnLoginTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!IsLoginVisible || _loginInProgress)
            {
                return;
            }

            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            {
                return;
            }

            e.Handled = true;
            _scanBuffer.Append(e.Text);
            _scanIdleTimer.Stop();
            _scanIdleTimer.Start();
        }

        // The scanner sends Enter after the ID, which is the signal to log in.
        private async void OnLoginKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsLoginVisible || (e.Key != Key.Enter && e.Key != Key.Return))
            {
                return;
            }

            e.Handled = true;
            _scanIdleTimer.Stop();

            string operatorId = _scanBuffer.ToString().Trim();
            _scanBuffer.Clear();

            if (operatorId.Length == 0 || _loginInProgress)
            {
                return;
            }

            await LoginWithOperatorIdAsync(operatorId);
        }

        // Start is allowed only when connected, not running, a serial is entered, and its profile is loaded.
        // Must run on the UI thread.
        private bool CanStartCalibration()
        {
            return _isConnected && !_isAutoTareRunning && _profileLoaded
                   && !string.IsNullOrWhiteSpace(txtSerial.Text);
        }

        // Safe to call from any thread
        private void SetStartEnabled(bool enabled)
        {
            Dispatcher.BeginInvoke(new Action(() => btnStart.IsEnabled = enabled && CanStartCalibration()));
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
                SetCommandButtonsEnabled(true);
                SetStartEnabled(true);
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

        private bool SendCommand(string command, bool discardIncomingFirst = false)
        {
            if (!_isConnected || string.IsNullOrEmpty(command))
            {
                return false;
            }

            SerialPort port = _serialPort;
            if (port == null || !port.IsOpen)
            {
                return false;
            }

            lock (_portWriteLock)
            {
                if (!_isConnected || !port.IsOpen)
                {
                    return false;
                }

                if (discardIncomingFirst)
                {
                    port.DiscardInBuffer();
                }

                port.Write(command);
                return true;
            }
        }

        // Sends RT once after connecting and checks the LDU's reply
        private bool SendResetAndCheck()
        {
            SerialPort port = _serialPort;
            if (port == null || !port.IsOpen)
            {
                return false;
            }

            if (!SendCommand(_resetCommand, discardIncomingFirst: true))
            {
                return false;
            }

            Console.WriteLine("Write " + _resetCommand);
            Thread.Sleep(_resetResponseWaitMs);

            string response;
            lock (_portWriteLock)
            {
                response = port.IsOpen ? port.ReadExisting() : string.Empty;
            }

            Console.WriteLine("RT response: " + response.Trim());
            return response.IndexOf(_resetSuccessToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PollLduWorker()
        {
            Console.WriteLine("LDU Polling Worker Thread Started");

            if (!SendResetAndCheck())
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show("LDU did not return success for the Reset (RT) command.", "Serial Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                    Disconnect();
                }));
                return;
            }

            while (_isConnected && !_stopEvent.WaitOne(0))
            {
                if (_pollingPaused)
                {
                    if (_stopEvent.WaitOne(10))
                    {
                        break;
                    }
                    continue;
                }

                Console.WriteLine("Polling LDU...");
                SerialPort port = _serialPort;
                if (port == null || !port.IsOpen)
                {
                    break;
                }

                try
                {
                    if (!SendCommand(_pollCommand, discardIncomingFirst: true))
                    {
                        break;
                    }

                    Thread.Sleep(20); // Allow time for the LDU to respond

                    Console.WriteLine("Write " + _pollCommand);

                    string line;
                    lock (_portWriteLock)
                    {
                        line = port.IsOpen ? port.ReadExisting() : string.Empty;
                    }

                    Console.WriteLine("Read " + line);

                    if (!string.IsNullOrEmpty(line))
                    {
                        double numericValue;
                        string formatted = FormatMillivolts(line, out numericValue, out bool parsedNumeric);
                        if (parsedNumeric)
                        {
                            lock (_readingLock)
                            {
                                _latestNumericValue = numericValue;
                                _hasNumericValue = true;
                                _readingSequence++;
                            }
                        }

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
            lblDisplay.Content = BuildReadingText(_latestFormattedReading);
        }

        private static TextBlock BuildReadingText(string formatted)
        {
            string value = formatted;
            string unit = string.Empty;

            int space = formatted.LastIndexOf(' ');
            if (space > 0)
            {
                value = formatted.Substring(0, space);
                unit = formatted.Substring(space);
            }

            var text = new TextBlock();
            text.Inlines.Add(new Run(value));
            text.Inlines.Add(new Run(unit) { FontSize = 35, FontWeight = FontWeights.Bold });
            return text;
        }

        // ---------- UI helpers (safe to call from the worker thread) ----------

        private void SetDisplayColor(Brush brush)
        {
            Dispatcher.BeginInvoke(new Action(() => lblDisplay.Foreground = brush));
        }

        private void SetStatus(string text, Brush brush = null)
        {
            Console.WriteLine("Status: " + text);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lblStatus.Content = text;
                lblStatus.Foreground = brush ?? Brushes.Black;
                lblStatus.FontWeight = FontWeights.Bold;
            }));
        }

        private void UpdateCountdownLabel(int secondsRemaining)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                lblTareCountDown.Content = secondsRemaining.ToString(CultureInfo.InvariantCulture);
            }));
        }

        private void SetR1(double value)
        {
            Dispatcher.BeginInvoke(new Action(() => lblR1.Content = FormatValue(value)));
        }

        private void SetR2(double value)
        {
            Dispatcher.BeginInvoke(new Action(() => lblR2.Content = FormatValue(value)));
        }

        private void ShowResult(double difference)
        {
            double rounded = Math.Round(difference, 5);
            bool inRange = rounded >= _resultMin && rounded <= _resultMax;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                lblResult.Content = FormatValue(rounded);
                lblResult.Foreground = inRange ? Brushes.Green : Brushes.Red;
            }));
        }

        private void SetLimitsEditable(bool editable)
        {
            Dispatcher.BeginInvoke(new Action(() => pnlLimits.IsEnabled = editable));
        }

        private void SetStopEnabled(bool enabled)
        {
            Dispatcher.BeginInvoke(new Action(() => btnStop.IsEnabled = enabled));
        }

        // Starts or stops the countdown panel blink. Safe to call from the worker thread.
        private void SetCountdownBlink(bool on)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_blinkTimer == null)
                {
                    _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    _blinkTimer.Tick += (s, e) =>
                    {
                        _blinkOn = !_blinkOn;
                        SetPanelColor(_blinkOn ? _blinkAlert : _blinkIdle);
                    };
                }

                if (on)
                {
                    _blinkOn = false;
                    _blinkTimer.Start();
                }
                else
                {
                    _blinkTimer.Stop();
                    SetPanelColor(_blinkIdle);
                }
            }));
        }

        // Sets the countdown panel background. Only call on the UI thread.
        private void SetPanelColor(Color color)
        {
            pnlCountdown.Background = new SolidColorBrush(color);
        }

        private static string FormatValue(double value)
        {
            return value.ToString("0.00000", CultureInfo.InvariantCulture);
        }

        // ---------- Parsing ----------

        private static string FormatMillivolts(string raw)
        {
            double ignoredValue;
            bool ignoredFlag;
            return FormatMillivolts(raw, out ignoredValue, out ignoredFlag);
        }

        private static string FormatMillivolts(string raw, out double numericValue, out bool parsedNumeric)
        {
            string trimmed = raw.Trim();
            Match match = _numberRegex.Match(trimmed);

            if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out numericValue))
            {
                parsedNumeric = true;
                return numericValue.ToString("0.00000", CultureInfo.InvariantCulture) + " mV/V";
            }

            numericValue = 0.0;
            parsedNumeric = false;
            return trimmed;
        }

        private bool TryGetLatestReading(out double value, out long sequence)
        {
            lock (_readingLock)
            {
                value = _latestNumericValue;
                sequence = _readingSequence;
                return _hasNumericValue;
            }
        }

        private bool IsZeroReading(double value)
        {
            return Math.Abs(value) <= _zeroTolerance;
        }

        private bool IsTareZero(double value)
        {
            return Math.Abs(value) <= _tareZeroTolerance;
        }

        private bool IsAutoTareCancelled()
        {
            return _autoTareStopEvent.WaitOne(0) || !_isConnected || _stopEvent.WaitOne(0);
        }

        // ---------- Reading wait helpers ----------

        // Returns true once _requiredStableSamples consecutive new readings stay within _stabilityTolerance.
        // timeoutMs < 0 waits indefinitely (until cancelled).
        private bool WaitForStableReading(int timeoutMs, out double stableValue)
        {
            DateTime start = DateTime.UtcNow;
            long lastSequence = -1;
            double referenceValue = 0.0;
            int stableCount = 0;
            stableValue = 0.0;

            while (!IsAutoTareCancelled())
            {
                if (timeoutMs >= 0 && (DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                {
                    return false;
                }

                double current;
                long seq;
                if (TryGetLatestReading(out current, out seq) && seq != lastSequence)
                {
                    lastSequence = seq;

                    if (stableCount == 0 || Math.Abs(current - referenceValue) > _stabilityTolerance)
                    {
                        referenceValue = current;
                        stableCount = 1;
                    }
                    else
                    {
                        stableCount++;
                        if (stableCount >= _requiredStableSamples)
                        {
                            stableValue = current;
                            return true;
                        }
                    }
                }

                if (_autoTareStopEvent.WaitOne(25))
                {
                    return false;
                }
            }

            return false;
        }

        // Waits until a new reading satisfies the condition. Waits indefinitely (until cancelled).
        private bool WaitForReading(Func<double, bool> condition, out double matchedValue)
        {
            long lastSequence = -1;
            matchedValue = 0.0;

            while (!IsAutoTareCancelled())
            {
                double current;
                long seq;
                if (TryGetLatestReading(out current, out seq) && seq != lastSequence)
                {
                    lastSequence = seq;
                    if (condition(current))
                    {
                        matchedValue = current;
                        return true;
                    }
                }

                if (_autoTareStopEvent.WaitOne(25))
                {
                    return false;
                }
            }

            return false;
        }

        // Runs the range hold and always stops the countdown blink when it finishes
        private bool WaitForRangeHold(double min, double max, int holdMs, string prompt)
        {
            try
            {
                return WaitForRangeHoldCore(min, max, holdMs, prompt);
            }
            finally
            {
                SetCountdownBlink(false);
            }
        }

        // Waits until the reading has stayed inside [min, max] for holdMs.
        // Shows a countdown and blinks the panel while it stays in range.
        // If the reading leaves the range, the countdown resets and the prompt is shown again.
        // Waits until the reading is inside [min, max] AND stable for holdMs.
        // A reading that is in range but still moving does not start the hold. No tare is sent in this step.
        private bool WaitForRangeHoldCore(double min, double max, int holdMs, string prompt)
        {
            bool colourIsGreen = false;
            long lastSequence = -1;
            bool wasInRange = false;
            bool colourSet = false;
            DateTime enteredAt = DateTime.MinValue;
            int lastShownSecond = -1;

            // Stability tracking, same rule as WaitForStableReading
            double stableRef = 0.0;
            int stableCount = 0;

            while (!IsAutoTareCancelled())
            {
                double current;
                long seq;
                if (TryGetLatestReading(out current, out seq) && seq != lastSequence)
                {
                    lastSequence = seq;

                    if (stableCount == 0 || Math.Abs(current - stableRef) > _stabilityTolerance)
                    {
                        stableRef = current;
                        stableCount = 1;
                    }
                    else
                    {
                        stableCount++;
                    }

                    bool inBand = current >= min && current <= max;
                    bool stable = stableCount >= _requiredStableSamples;
                    bool inRange = inBand && stable;

                    if (inRange)
                    {
                        if (!colourIsGreen)
                        {
                            SetDisplayColor(Brushes.Green);
                            colourIsGreen = true;
                        }
                        colourSet = true;
                    }
                    else if (!inBand)
                    {
                        if (!colourSet || colourIsGreen)
                        {
                            SetDisplayColor(Brushes.Red);
                            colourIsGreen = false;
                        }
                        colourSet = true;
                    }

                    if (inRange && !wasInRange)
                    {
                        enteredAt = DateTime.UtcNow;
                        lastShownSecond = -1;
                        SetStatus("In Range. Hold steady.", Brushes.DarkOrange);
                        SetCountdownBlink(true);
                    }
                    else if (!inRange && wasInRange)
                    {
                        lastShownSecond = -1;
                        UpdateCountdownLabel(_tareCountdownSeconds);
                        SetStatus(prompt);
                        SetCountdownBlink(false);
                    }
                    else if (!inRange && inBand && !stable)
                    {
                        // In the band but still moving: tell the operator to wait, without tare
                        SetStatus("Reading not stable. Wait...", Brushes.DarkOrange);
                    }

                    wasInRange = inRange;
                }

                if (wasInRange)
                {
                    double elapsedMs = (DateTime.UtcNow - enteredAt).TotalMilliseconds;
                    int remaining = (int)Math.Ceiling((holdMs - elapsedMs) / 1000.0);
                    if (remaining < 0)
                    {
                        remaining = 0;
                    }

                    // Update the label only when the displayed second changes
                    if (remaining != lastShownSecond)
                    {
                        lastShownSecond = remaining;
                        UpdateCountdownLabel(remaining);
                    }

                    if (elapsedMs >= holdMs)
                    {
                        return true;
                    }
                }

                if (_autoTareStopEvent.WaitOne(25))
                {
                    return false;
                }
            }

            return false;
        }
        // Waits until the reading falls by dropAmount below referenceValue.
        // While waiting, the status shows the prompt (for example "Step 6 of 8: Remove 22lb.").
        private bool WaitForUnloadDrop(double referenceValue, double dropAmount, string prompt)
        {
            double triggerValue = referenceValue - dropAmount;
            long lastSequence = -1;
            bool? lastState = null;

            while (!IsAutoTareCancelled())
            {
                double current;
                long seq;
                if (TryGetLatestReading(out current, out seq) && seq != lastSequence)
                {
                    lastSequence = seq;

                    if (current < triggerValue)
                    {
                        SetStatus("Unload detected (" + FormatValue(current) + ").", Brushes.DarkOrange);
                        return true;
                    }

                    // Update the status only when the state changes, so it doesn't flicker
                    if (lastState != false)
                    {
                        SetStatus(prompt + ".", Brushes.Red);
                        lastState = false;
                    }
                }

                if (_autoTareStopEvent.WaitOne(25))
                {
                    return false;
                }
            }

            return false;
        }

        // Waits for a fixed time without updating the UI. Returns false if cancelled.
        private bool WaitForSilent(int ms)
        {
            return !_autoTareStopEvent.WaitOne(ms) && !IsAutoTareCancelled();
        }

        // Counts down from seconds to 0 and updates lblTareCountDown.
        // The countdown panel blinks for the whole countdown. Returns false if cancelled.
        private bool RunCountdown(int seconds)
        {
            SetCountdownBlink(true);
            try
            {
                for (int remaining = seconds; remaining >= 0; remaining--)
                {
                    if (IsAutoTareCancelled())
                    {
                        return false;
                    }

                    UpdateCountdownLabel(remaining);

                    if (remaining > 0 && _autoTareStopEvent.WaitOne(1000))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                // Stops the blink on every exit path: finished, cancelled, or error
                SetCountdownBlink(false);
            }
        }

        // Sends ST, pauses polling while the reply is read, then waits the countdown so the LDU can settle
        private bool SendTareAndWait()
        {
            _pollingPaused = true;
            try
            {
                Thread.Sleep(100); // let the current poll cycle finish

                lock (_portWriteLock)
                {
                    SerialPort port = _serialPort;
                    if (port == null || !port.IsOpen)
                    {
                        SetStatus("Connection lost while sending ST.", Brushes.Red);
                        return false;
                    }

                    port.DiscardInBuffer();
                    port.Write(_tareCommand);
                }
                Console.WriteLine("Write " + _tareCommand);

                Thread.Sleep(300); // wait for the LDU reply

                string reply;
                lock (_portWriteLock)
                {
                    reply = (_serialPort != null && _serialPort.IsOpen) ? _serialPort.ReadExisting() : string.Empty;
                }
                Console.WriteLine("ST reply: [" + reply.Trim() + "]");
            }
            finally
            {
                _pollingPaused = false;
            }

            return RunCountdown(_tareCountdownSeconds);
        }

        // Checks for a stable reading that passes accept(). If it fails, sends ST, waits
        // and checks again, up to _maxTareAttempts times.
        private bool StabilizeWithTare(string purpose, Func<double, bool> accept, out double stableValue)
        {
            stableValue = 0.0;

            for (int attempt = 0; attempt <= _maxTareAttempts; attempt++)
            {
                if (IsAutoTareCancelled())
                {
                    return false;
                }

                // Attempt 0 only checks stability, so no tare is sent when the reading is already good
                if (attempt > 0)
                {
                    SetStatus("Wait: Taring...", Brushes.DarkOrange);
                    if (!SendTareAndWait())
                    {
                        return false;
                    }
                }

                if (WaitForStableReading(_stabilityTimeoutMs, out stableValue) && accept(stableValue))
                {
                    return true;
                }
            }

            return false;
        }

        // ---------- Calibration sequence ----------

        private void RunCalibrationWorker()
        {
            _isAutoTareRunning = true;

            // Copy the load name once so the text stays the same for the whole run
            string load = _loadLabel;

            try
            {
                UpdateCountdownLabel(_tareCountdownSeconds);

                // Step 1: zero the reading
                double zeroValue;
                if (!StabilizeWithTare("Zeroing", IsZeroReading, out zeroValue))
                {
                    if (!IsAutoTareCancelled())
                    {
                        SetStatus("Reading Unstable. Retry again.", Brushes.Red);
                    }
                    return;
                }

                // Step 2: apply the load and wait for it to be detected
                SetStatus("Apply load " + load + ".", Brushes.Red);

                double ignored;
                if (!WaitForReading(v => v > zeroValue + _loadDetectThreshold, out ignored))
                {
                    return;
                }

                // Step 3: store R1 once the reading is stable
                SetStatus("Waiting for stable reading... (R1)", Brushes.DarkOrange);
                double r1;
                if (!WaitForStableReading(-1, out r1))
                {
                    return;
                }

                SetR1(r1);
                SetStatus("R1 stored at " + FormatValue(r1) + ". Tare in progress.", Brushes.DarkOrange);

                // Step 4: tare with the load on
                double afterTare;
                if (!StabilizeWithTare("tare after R1", IsTareZero, out afterTare))
                {
                    if (!IsAutoTareCancelled())
                    {
                        SetStatus("Tare Faild " + _maxTareAttempts + " ST attempts.", Brushes.Red);
                    }
                    return;
                }

                // Step 5: adjust the screw into range
                string screwPrompt = "Adjust screw to between " + FormatValue(_screwMin) +
                                     " and " + FormatValue(_screwMax) + ".";
                SetStatus(screwPrompt, Brushes.Red);

                if (!WaitForRangeHold(_screwMin, _screwMax, _screwHoldMs, screwPrompt))
                {
                    return;
                }

                // Step 6: short silent wait, then remove the load
                if (!WaitForSilent(_screwPostWaitMs))
                {
                    return;
                }

                double referenceValue;
                long ignoredSeq;
                TryGetLatestReading(out referenceValue, out ignoredSeq);

                string removePrompt = "Unload " + load + ".";
                SetStatus(removePrompt, Brushes.Red);

                if (!WaitForUnloadDrop(referenceValue, _unloadDropThreshold, removePrompt))
                {
                    return;
                }

                // Step 7: wait for the unloaded reading to settle, then tare
                SetStatus("Waiting for stable reading...", Brushes.DarkOrange);

                double unloadedValue;
                if (!WaitForStableReading(-1, out unloadedValue))
                {
                    return;
                }

                if (!SendTareAndWait())
                {
                    return;
                }

                double tareZeroValue;
                if (!StabilizeWithTare("Tare after removal", IsTareZero, out tareZeroValue))
                {
                    if (!IsAutoTareCancelled())
                    {
                        SetStatus("Tare failed. after " + _maxTareAttempts + " ST attempts.", Brushes.Red);
                    }
                    return;
                }

                // Step 8: apply the load again and measure R2
                SetStatus("Apply " + load, Brushes.Red);

                if (!WaitForReading(v => v > tareZeroValue + _loadDetectThreshold, out ignored))
                {
                    return;
                }

                SetStatus("Waiting " + _tareCountdownSeconds + " s before measuring R2...", Brushes.DarkOrange);
                if (!RunCountdown(_tareCountdownSeconds))
                {
                    return;
                }

                SetStatus("Waiting for stable reading (R2)...", Brushes.DarkOrange);
                double r2;
                if (!WaitForStableReading(-1, out r2))
                {
                    return;
                }

                SetR2(r2);

                // Result: R1 - R2 must be within the result limits
                double difference = r1 - r2;
                ShowResult(difference);

                double rounded = Math.Round(difference, 5);
                bool pass = rounded >= _resultMin && rounded <= _resultMax;

                if (!pass)
                {
                    // Failed test: do not save, show the popup, and let the operator retry
                    SetStatus("Test failed. R1 - R2 = " + FormatValue(rounded) + " is outside range. Retry.", Brushes.Red);
                    ShowTestFailedPopup(rounded);
                    return;
                }

                SetStatus("Complete. R1 - R2 = " + FormatValue(rounded) + " (within range).", Brushes.Green);
                SaveRecordAndNotify(r1, r2, difference);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Calibration error: " + ex.Message);
                SetStatus("Error: " + ex.Message, Brushes.Red);
            }
            finally
            {
                _isAutoTareRunning = false;
                SetCountdownBlink(false);
                SetLimitsEditable(true);
                SetStopEnabled(false);
                SetStartEnabled(true);
                Dispatcher.BeginInvoke(new Action(() => lblDisplay.Foreground = _defaultDisplayBrush));
                SetInputsLocked(false);
            }
        }

        private void CancelAutoTare()
        {
            _autoTareStopEvent.Set();
            Thread autoTareWorker = _autoTareThread;
            _autoTareThread = null;
            if (autoTareWorker != null && autoTareWorker.IsAlive)
            {
                autoTareWorker.Join(500);
            }
        }

        private void Disconnect()
        {
            CancelAutoTare();
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
                lock (_portWriteLock)
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
            }

            _uiUpdatePending = false;
            btnConnect.Content = "Connect";
            SetCommandButtonsEnabled(false);
            SetStartEnabled(false);
            SetStopEnabled(false);
            cmbPort.IsEnabled = true;
            cmbBaudRate.IsEnabled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            _stopEvent.Close();
            _autoTareStopEvent.Close();
            base.OnClosed(e);
        }

        private void btnTare_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SendCommand(_tareCommand))
                {
                    MessageBox.Show("Connect to the LDU before sending Tare.", "Serial Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Console.WriteLine("Write " + _tareCommand);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to send Tare command: " + ex.Message, "Serial Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SendCommand(_resetCommand))
                {
                    MessageBox.Show("Connect to the LDU before sending Reset.", "Serial Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Console.WriteLine("Write " + _resetCommand);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to send Reset command: " + ex.Message, "Serial Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSerial.Text))
            {
                MessageBox.Show("Enter a serial number before starting.", "Serial Number",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isConnected)
            {
                MessageBox.Show("Connect to the LDU before starting.", "Serial Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_profileLoaded)
            {
                MessageBox.Show("Look up the serial number first so its profile can be loaded.", "Profile",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_isAutoTareRunning)
            {
                return;
            }

            // Read the screw and result limits from the text boxes (UI thread)
            if (!ReadLimitsFromTextBoxes())
            {
                return;
            }

            // Read the applied load and save fields (UI thread only)
            string loadText = txtApplyLoad.Text.Trim();
            _loadLabel = string.IsNullOrEmpty(loadText) ? "the load" : loadText;
            _serialNumber = txtSerial.Text.Trim();
            _operatorIdText = _currentOperator != null
                ? (!string.IsNullOrWhiteSpace(_currentOperator.EmployeeNumber)
                    ? _currentOperator.EmployeeNumber
                    : _currentOperator.EmpNo)
                : string.Empty;

            SetLimitsEditable(false);
            SetStopEnabled(true);
            SetStartEnabled(false);
            SetInputsLocked(true);

            // Clear previous results
            lblR1.Content = "--";
            lblR2.Content = "--";
            lblResult.Content = "--";
            lblResult.Foreground = Brushes.Black;
            lblStatus.Content = "Starting...";

            CancelAutoTare();
            _autoTareStopEvent.Reset();

            _autoTareThread = new Thread(RunCalibrationWorker);
            _autoTareThread.IsBackground = true;
            _autoTareThread.Name = "LduCalibrationWorker";
            _autoTareThread.Start();
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            CancelAutoTare();
            SetStatus("Stopped.", Brushes.Red);
            SetStartEnabled(true);
        }

        private void SetCommandButtonsEnabled(bool enabled)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                btnTare.IsEnabled = enabled;
                btnReset.IsEnabled = enabled;
            }));
        }

        // ---------- Serial lookup ----------

        private void txtSerial_LostFocus(object sender, RoutedEventArgs e)
        {
            LookupSerialProfile();
        }

        private void txtSerial_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                LookupSerialProfile();
            }
        }

        // Finds the serial in WIP, then loads the active overload-stop profile for its S3 part number
        private void LookupSerialProfile()
        {
            string serial = txtSerial.Text.Trim();
            if (string.IsNullOrEmpty(serial))
            {
                return;
            }

            _profileLoaded = false;
            _profileId = string.Empty;
            try
            {
                using (IDbConnection conn = _db.GetOpenConnection())
                {
                    string s3PartNo;

                    using (IDbCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT S3CellPartNo FROM " + _wipTable +
                                          " WHERE SerialNo = @serial LIMIT 1";
                        AddParam(cmd, "@serial", serial);

                        object result = cmd.ExecuteScalar();
                        if (result == null || result == DBNull.Value)
                        {
                            SetStatus("Serial " + serial + " not found in WIP.", Brushes.Red);
                            MessageBox.Show("Serial number " + serial + " was not found in WIP.",
                                            "Serial Number", MessageBoxButton.OK, MessageBoxImage.Warning);
                            SetStartEnabled(true);
                            return;
                        }

                        s3PartNo = result.ToString();
                    }

                    using (IDbCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id, applyload, screw_min, screw_max, output_min, output_max " +
                                          "FROM tbloverloadstopparameters " +
                                          "WHERE S3CellPartNo = @part AND isInctive = 0 " +
                                          "ORDER BY id DESC LIMIT 1";
                        AddParam(cmd, "@part", s3PartNo);

                        using (IDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                SetStatus("No valid profile for " + s3PartNo + ".", Brushes.Red);
                                MessageBox.Show("No valid overload stop profile found for S3 part " + s3PartNo + ".",
                                                "Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                                SetStartEnabled(true);
                                return;
                            }

                            _profileId = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture);
                            txtApplyLoad.Text = reader.GetString(1);
                            txtScrewMin.Text = reader.GetDecimal(2).ToString("0.00000", CultureInfo.InvariantCulture);
                            txtScrewMax.Text = reader.GetDecimal(3).ToString("0.00000", CultureInfo.InvariantCulture);
                            txtResultMin.Text = reader.GetDecimal(4).ToString("0.00000", CultureInfo.InvariantCulture);
                            txtResultMax.Text = reader.GetDecimal(5).ToString("0.00000", CultureInfo.InvariantCulture);
                        }
                    }

                    _profileLoaded = true;
                    SetStatus("Profile loaded for " + s3PartNo + ".", Brushes.Green);
                    SetStartEnabled(true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Lookup failed.", Brushes.Red);
                MessageBox.Show("Failed to look up the serial number:\n" + ex.Message,
                                "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStartEnabled(true);
            }
        }

        private static bool TryParseTextBox(TextBox box, string name, out double value)
        {
            if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            MessageBox.Show(name + " is not a valid number.", "Limits", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Must run on the UI thread. Copies the text box values into the fields used by the worker.
        private bool ReadLimitsFromTextBoxes()
        {
            double screwMin, screwMax, resultMin, resultMax;

            if (!TryParseTextBox(txtScrewMin, "Screw min", out screwMin) ||
                !TryParseTextBox(txtScrewMax, "Screw max", out screwMax) ||
                !TryParseTextBox(txtResultMin, "Result min", out resultMin) ||
                !TryParseTextBox(txtResultMax, "Result max", out resultMax))
            {
                return false;
            }

            if (screwMin > screwMax)
            {
                MessageBox.Show("Screw min must be less than or equal to Screw max.", "Limits", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (resultMin > resultMax)
            {
                MessageBox.Show("Result min must be less than or equal to Result max.", "Limits", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _screwMin = screwMin;
            _screwMax = screwMax;
            _resultMin = resultMin;
            _resultMax = resultMax;
            return true;
        }

        private async Task LoginWithOperatorIdAsync(string operatorId)
        {
            _loginInProgress = true;
            lblLoginMessage.Visibility = Visibility.Collapsed;
            lblScanStatus.Text = "Checking operator...";

            try
            {
                string url = $"http://192.168.0.25:3000/api/v1/deffect/operator/{Uri.EscapeDataString(operatorId)}";
                using (HttpResponseMessage response = await _httpClient.GetAsync(url))
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<OperatorApiResponse>(json);

                    if (response.IsSuccessStatusCode && result != null && result.Success && result.Data != null)
                    {
                        _currentOperator = result.Data;

                        string resolvedId = !string.IsNullOrWhiteSpace(_currentOperator.EmployeeNumber)
                            ? _currentOperator.EmployeeNumber
                            : (!string.IsNullOrWhiteSpace(_currentOperator.EmpNo) ? _currentOperator.EmpNo : operatorId);

                        string resolvedName = !string.IsNullOrWhiteSpace(_currentOperator.CallingName)
                            ? _currentOperator.CallingName
                            : _currentOperator.Name;

                        SetOperatorInfo(resolvedId, resolvedName);

                        gridLogin.Visibility = Visibility.Collapsed;
                        mainGrid.Visibility = Visibility.Visible;
                        mainTabs.SelectedIndex = 0;
                    }
                    else
                    {
                        ShowLoginError("Invalid operator ID: " + operatorId + ". Please scan again.");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowLoginError("Connection error: " + ex.Message);
            }
            finally
            {
                _loginInProgress = false;
                lblScanStatus.Text = "Scan your operator badge";
            }
        }

        private void btnLogout_Click(object sender, RoutedEventArgs e)
        {
            _currentOperator = null;
            lblOperatorInfo.Text = string.Empty;
            _scanBuffer.Clear();
            lblLoginMessage.Visibility = Visibility.Collapsed;
            lblScanStatus.Text = "Scan your operator badge";

            mainGrid.Visibility = Visibility.Collapsed;
            gridLogin.Visibility = Visibility.Visible;
            FocusLoginScanner();
        }

        private void ShowLoginError(string message)
        {
            lblLoginMessage.Text = message;
            lblLoginMessage.Visibility = Visibility.Visible;
        }

        // Name on the first line (larger, bold), operator ID on the second line (smaller, muted)
        private void SetOperatorInfo(string operatorId, string name)
        {
            lblOperatorInfo.Inlines.Clear();

            if (!string.IsNullOrWhiteSpace(name))
            {
                lblOperatorInfo.Inlines.Add(new Run(name)
                {
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextDark")
                });
                lblOperatorInfo.Inlines.Add(new LineBreak());
            }

            lblOperatorInfo.Inlines.Add(new Run(operatorId)
            {
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                Foreground = (Brush)FindResource("TextMuted")
            });
        }

        // ---------- Data tab ----------

        private void mainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore selection changes bubbling up from controls inside the tabs
            if (!ReferenceEquals(e.OriginalSource, mainTabs))
            {
                return;
            }

            if (mainTabs.SelectedIndex == 1)
            {
                LoadMeasurementData(txtDataSerial.Text);
            }
        }

        private void btnDataSearch_Click(object sender, RoutedEventArgs e)
        {
            LoadMeasurementData(txtDataSerial.Text);
        }

        private void btnDataRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadMeasurementData(txtDataSerial.Text);
        }

        private void LoadMeasurementData(string serialFilter)
        {
            try
            {
                var table = new DataTable();

                using (IDbConnection conn = _db.GetOpenConnection())
                using (IDbCommand cmd = conn.CreateCommand())
                {
                    bool filter = !string.IsNullOrWhiteSpace(serialFilter);

                    cmd.CommandText = "SELECT * FROM tbloverloadstopmeasurements " +
                                      (filter ? "WHERE serial_number LIKE @serial " : string.Empty) +
                                      "ORDER BY id DESC LIMIT 500";

                    if (filter)
                    {
                        AddParam(cmd, "@serial", "%" + serialFilter.Trim() + "%");
                    }

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        table.Load(reader);
                    }
                }

                dgData.ItemsSource = table.DefaultView;
                lblDataInfo.Text = table.Rows.Count + " record(s) loaded (latest 500).";
            }
            catch (Exception ex)
            {
                dgData.ItemsSource = null;
                lblDataInfo.Text = "Load failed: " + ex.Message;
            }
        }

        // ---------- Database ----------

        private void SaveRecordAndNotify(double r1, double r2, double difference)
        {
            try
            {
                SaveCalibrationRecord(r1, r2, difference, _serialNumber, _operatorIdText, _profileId);
                SetStatus("Test Pass & Data Saved.", Brushes.Green);

                Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show("Calibration record saved successfully.", "Save",
                                    MessageBoxButton.OK, MessageBoxImage.Information)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Save error: " + ex.Message);
                SetStatus("Save failed: " + ex.Message, Brushes.Red);

                Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show("Failed to save the calibration record:\n" + ex.Message,
                                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error)));
            }
        }

        private void SaveCalibrationRecord(double r1, double r2, double diff,
                                   string serial, string operatorIdText, string profileId)
        {
            int operatorId;
            int.TryParse(operatorIdText, out operatorId);

            const string sql = @"INSERT INTO `tbloverloadstopmeasurements`
        (`serial_number`, `operator_id`, `profile_id`, `load_first`, `load_second`, `load_differance`)
        VALUES
        (@serial_number, @operator_id, @profile_id, @load_first, @load_second, @load_differance)";

            using (IDbConnection conn = _db.GetOpenConnection())
            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                AddParam(cmd, "@serial_number", serial);
                AddParam(cmd, "@operator_id", operatorId);
                AddParam(cmd, "@profile_id", profileId);
                AddParam(cmd, "@load_first", r1);
                AddParam(cmd, "@load_second", r2);
                AddParam(cmd, "@load_differance", diff);
                cmd.ExecuteNonQuery();
            }
        }

        private static void AddParam(IDbCommand cmd, string name, object value)
        {
            IDbDataParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        // ---------- Operator API models ----------

        private sealed class OperatorApiResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("data")]
            public OperatorInfo Data { get; set; }
        }

        private sealed class OperatorInfo
        {
            [JsonPropertyName("employee_number")]
            public string EmployeeNumber { get; set; }

            [JsonPropertyName("empNo")]
            public string EmpNo { get; set; }

            [JsonPropertyName("calling_name")]
            public string CallingName { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("department")]
            public string Department { get; set; }
        }

        private void SetInputsLocked(bool locked)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtSerial.IsReadOnly = locked;
                btnConnect.IsEnabled = !locked;
            }));
        }
        private void ShowTestFailedPopup(double difference)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ShowRetryDialog(difference))
                {
                    btnStart_Click(this, new RoutedEventArgs());
                }
            }));
        }
        private bool ShowRetryDialog(double difference)
        {
            bool retry = false;

            var dialog = new Window
            {
                Title = "Test Failed",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                FontFamily = this.FontFamily,
                FontSize = 13
            };

            var message = new TextBlock
            {
                Text = "TEST FAILED\n\n" +
                       "R1 - R2 = " + FormatValue(difference) + "\n" +
                       "Allowed range: " + FormatValue(_resultMin) + " to " + FormatValue(_resultMax) + "\n\n" +
                       "The result was not saved.",
                TextWrapping = TextWrapping.Wrap,
                Width = 340,
                Margin = new Thickness(0, 0, 0, 20),
                Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x28))
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Style = (Style)FindResource("SecondaryButton"),
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };

            var retryButton = new Button
            {
                Content = "Retry",
                Style = (Style)FindResource("PrimaryButton"),
                Width = 100,
                Height = 32,
                IsDefault = true
            };

            retryButton.Click += (s, e) =>
            {
                retry = true;
                dialog.Close();
            };

            cancelButton.Click += (s, e) => dialog.Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(retryButton);

            var content = new StackPanel { Margin = new Thickness(20) };
            content.Children.Add(message);
            content.Children.Add(buttons);

            dialog.Content = content;
            dialog.ShowDialog();

            return retry;
        }
    }
}