using System;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AOGBogballeBridge
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string SimulatorPort = "AOG Simulator (UDP)";

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<string> AvailablePorts { get; }
        public ObservableCollection<Brush> Sections { get; }
        public ObservableCollection<string> SectionRateDisplays { get; }
        public ObservableCollection<string> ShapefileFields { get; }

        private IReadOnlyList<ShapefileFeature> _shapefileFeatures = Array.Empty<ShapefileFeature>();
        private string? _selectedShapefileField;
        private string _shapefileName = "No shapefile loaded";
        private string _shapefileStatus = "Load an ESRI .shp file to preview its features.";
        private bool _isShapefileGeographic;
        private bool _shapefileLoaded;
        private bool _isLoadingShapefile;
        private bool _gpsConnected;
        private bool _gpsPositionValid;
        private double _gpsLatitude;
        private double _gpsLongitude;
        private int _gpsFixQuality;
        private int _gpsSatellites;
        private string _gpsStatus = "Waiting for PANDA data.";
        private bool _rateZoneValid;
        private string? _currentApplicationRate;
        private string? _lastSentApplicationRate;
        private bool _spreaderEnabledLast = true;

        private string? _selectedPort;
        private double _speed;
        private double _width;
        private double[] _sectionWidthsCm = Array.Empty<double>();
        private IReadOnlyList<double> _sectionWidthsMeters = Array.Empty<double>();
        private string _calibratorSetRateDisplay = "—";
        private double _toolbarOffsetMeters;
        private double _toolbarLatitude;
        private double _toolbarLongitude;
        private double _toolHeadingDegrees;
        private bool _toolHeadingValid;
        private DateTime _toolHeadingUpdatedAt;
        private string _toolHeadingSource = "unavailable";
        private Point? _previousGpsPosition;
        private DateTime _previousGpsPositionAt;
        private Point? _gpsOutPosition;
        private DateTime _lastGpsOutPositionAt;
        private bool _legacyRateFallbackUsed;
        private bool _udpConnected;
        private bool _serialConnected;
        private DateTime _lastUdp;
        private byte _currentSectionMask;
        private string _lastPgn = "";
        private string _configText = "";
        private string _simulatorOutput = "No Bogballe command received yet.";
        private string _rateProtocolStatus = "Waiting for a valid application rate.";
        private readonly object _serialReceiveLock = new();
        private string _serialReceiveBuffer = string.Empty;
        private string? _pendingFallbackCommand;
        private bool _appRatProtocolConfirmed;
        private bool _legacyRateProtocolConfirmed;

        // Machine configuration
        private int _secNum;
        private double _secWidth; // cm
        private bool _validConfig;
        private int _activeSectionsLast;
        private int _activeSectionMaskLast = -1;
        private int _aogVersion;
        private bool _versionWarned;
        private DateTime _lastGpsPacket;

        private UdpClient? _udp;
        private SerialPort? _serial;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _rateCommandAckTimer;
        private readonly DispatcherTimer _rateReadbackTimer;
        private readonly Dispatcher _dispatcher;

        public ICommand RefreshPortsCommand { get; }
        public ICommand LoadShapefileCommand { get; }

        public bool GpsConnected
        {
            get => _gpsConnected;
            private set
            {
                _gpsConnected = value;
                OnPropertyChanged(nameof(GpsConnected));
            }
        }

        public bool ShapefileLoaded
        {
            get => _shapefileLoaded;
            private set
            {
                _shapefileLoaded = value;
                OnPropertyChanged(nameof(ShapefileLoaded));
            }
        }

        public bool GpsPositionValid
        {
            get => _gpsPositionValid;
            private set
            {
                _gpsPositionValid = value;
                OnPropertyChanged(nameof(GpsPositionValid));
                OnPropertyChanged(nameof(GpsCoordinateText));
                OnPropertyChanged(nameof(ToolbarPositionValid));
            }
        }

        public double GpsLatitude
        {
            get => _gpsLatitude;
            private set
            {
                _gpsLatitude = value;
                OnPropertyChanged(nameof(GpsLatitude));
                OnPropertyChanged(nameof(GpsCoordinateText));
            }
        }

        public double GpsLongitude
        {
            get => _gpsLongitude;
            private set
            {
                _gpsLongitude = value;
                OnPropertyChanged(nameof(GpsLongitude));
                OnPropertyChanged(nameof(GpsCoordinateText));
            }
        }

        public double ToolbarLatitude => _toolbarLatitude;
        public double ToolbarLongitude => _toolbarLongitude;
        public double ToolHeadingDegrees => _toolHeadingDegrees;
        public string ToolHeadingStatus => _toolHeadingValid &&
            DateTime.UtcNow - _toolHeadingUpdatedAt <= TimeSpan.FromSeconds(3)
                ? $"Heading: {_toolHeadingDegrees:F0}° ({_toolHeadingSource})"
                : "Heading unavailable";
        public bool ToolbarPositionValid => GpsPositionValid && _gpsOutPosition.HasValue && _validConfig && _toolHeadingValid &&
            DateTime.UtcNow - _lastGpsOutPositionAt <= TimeSpan.FromSeconds(3) &&
            DateTime.UtcNow - _toolHeadingUpdatedAt <= TimeSpan.FromSeconds(3) && MachineWidthMeters > 0;
        public double MachineWidthMeters => _sectionWidthsCm.Sum() / 100.0;
        public IReadOnlyList<double> SectionWidthsMeters => _sectionWidthsMeters;

        public string GpsCoordinateText => GpsPositionValid
            ? $"Lat: {GpsLatitude:F6}   Lon: {GpsLongitude:F6}"
            : "Position unavailable";

        public int GpsFixQuality
        {
            get => _gpsFixQuality;
            private set
            {
                _gpsFixQuality = value;
                OnPropertyChanged(nameof(GpsFixQuality));
            }
        }

        public int GpsSatellites
        {
            get => _gpsSatellites;
            private set
            {
                _gpsSatellites = value;
                OnPropertyChanged(nameof(GpsSatellites));
            }
        }

        public string GpsStatus
        {
            get => _gpsStatus;
            private set
            {
                _gpsStatus = value;
                OnPropertyChanged(nameof(GpsStatus));
            }
        }

        public IReadOnlyList<ShapefileFeature> ShapefileFeatures
        {
            get => _shapefileFeatures;
            private set
            {
                _shapefileFeatures = value;
                OnPropertyChanged(nameof(ShapefileFeatures));
            }
        }

        public bool IsShapefileGeographic
        {
            get => _isShapefileGeographic;
            private set
            {
                _isShapefileGeographic = value;
                OnPropertyChanged(nameof(IsShapefileGeographic));
            }
        }

        public string? SelectedShapefileField
        {
            get => _selectedShapefileField;
            set
            {
                _selectedShapefileField = value;
                OnPropertyChanged(nameof(SelectedShapefileField));
                if (!_isLoadingShapefile)
                    UpdateApplicationRate();
            }
        }

        public string CurrentApplicationRate
        {
            get => !ShapefileLoaded
                ? "Shapefile rate control off; using normal spreader control."
                : _currentApplicationRate == null ? "No rate selected" : $"Application rate: {_currentApplicationRate}";
            private set
            {
                _currentApplicationRate = value;
                OnPropertyChanged(nameof(CurrentApplicationRate));
                OnPropertyChanged(nameof(TargetApplicationRateDisplay));
            }
        }

        public string TargetApplicationRateDisplay => !ShapefileLoaded
            ? "Off"
            : _currentApplicationRate ?? "—";

        public string ShapefileName
        {
            get => _shapefileName;
            private set
            {
                _shapefileName = value;
                OnPropertyChanged(nameof(ShapefileName));
            }
        }

        public string ShapefileStatus
        {
            get => _shapefileStatus;
            private set
            {
                _shapefileStatus = value;
                OnPropertyChanged(nameof(ShapefileStatus));
            }
        }

        public string? SelectedPort
        {
            get => _selectedPort;
            set
            {
                if (string.Equals(_selectedPort, value, StringComparison.Ordinal))
                    return;

                _selectedPort = value;
                Properties.Settings.Default.ComPort = value;
                Properties.Settings.Default.Save();
                OnPropertyChanged(nameof(SelectedPort));
                OnPropertyChanged(nameof(IsSimulatorMode));
                OnPropertyChanged(nameof(ConnectionOutputLabel));
                OnPropertyChanged(nameof(SerialBrush));
                OnPropertyChanged(nameof(SimulatorStatus));
                ConnectSerial();
            }
        }

        public bool IsSimulatorMode => string.Equals(SelectedPort, SimulatorPort, StringComparison.Ordinal);

        public string ConnectionOutputLabel => IsSimulatorMode ? "Simulator:" : "Serial:";

        public string SimulatorStatus => UdpConnected
            ? $"Live AOG traffic; virtual calibrator command: {_simulatorOutput}"
            : "Waiting for live AOG UDP traffic on port 8888; no data is generated.";

        public double Speed
        {
            get => _speed;
            set
            {
                _speed = value;
                OnPropertyChanged(nameof(Speed));
            }
        }

        public string CalibratorSetRateDisplay
        {
            get => _calibratorSetRateDisplay;
            private set
            {
                _calibratorSetRateDisplay = value;
                OnPropertyChanged(nameof(CalibratorSetRateDisplay));
            }
        }

        public string LegacyRateFallbackNote => _legacyRateFallbackUsed ? "UNIQ legacy used" : string.Empty;

        public double Width
        {
            get => _width;
            private set
            {
                _width = value;
                OnPropertyChanged(nameof(Width));
                OnPropertyChanged(nameof(ToolbarPositionValid));
            }
        }

        public double ToolbarOffsetMeters
        {
            get => _toolbarOffsetMeters;
            set
            {
                if (!double.IsFinite(value) || value is < -100 or > 100)
                    throw new ArgumentOutOfRangeException(nameof(value), "Toolbar offset must be between -100 and 100 meters.");
                if (Math.Abs(_toolbarOffsetMeters - value) < 0.001)
                    return;

                _toolbarOffsetMeters = value;
                Properties.Settings.Default.ToolbarOffsetMeters = value;
                Properties.Settings.Default.Save();
                OnPropertyChanged(nameof(ToolbarOffsetMeters));
                UpdateToolbarPosition();
                UpdateApplicationRate();
            }
        }

        public bool UdpConnected
        {
            get => _udpConnected;
            set
            {
                _udpConnected = value;
                OnPropertyChanged(nameof(UdpConnected));
                OnPropertyChanged(nameof(UdpBrush));
                OnPropertyChanged(nameof(SimulatorStatus));
            }
        }

        public bool SerialConnected
        {
            get => _serialConnected;
            set
            {
                _serialConnected = value;
                OnPropertyChanged(nameof(SerialConnected));
                OnPropertyChanged(nameof(SerialBrush));
                if (!value)
                {
                    _rateReadbackTimer.Stop();
                    CalibratorSetRateDisplay = "—";
                    SetLegacyRateFallbackUsed(false);
                }
            }
        }

        public string LastPgn
        {
            get => _lastPgn;
            set
            {
                _lastPgn = value;
                OnPropertyChanged(nameof(LastPgn));
            }
        }

        public string ConfigText
        {
            get => _configText;
            set
            {
                _configText = value;
                OnPropertyChanged(nameof(ConfigText));
            }
        }

        public string RateProtocolStatus
        {
            get => _rateProtocolStatus;
            private set
            {
                _rateProtocolStatus = value;
                OnPropertyChanged(nameof(RateProtocolStatus));
            }
        }

        public Brush UdpBrush => UdpConnected ? Brushes.LimeGreen : Brushes.Red;
        public Brush SerialBrush => IsSimulatorMode
            ? Brushes.DodgerBlue
            : SerialConnected ? Brushes.LimeGreen : Brushes.Red;

        public MainViewModel()
        {
            _dispatcher = Application.Current.Dispatcher;
            _rateCommandAckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _rateCommandAckTimer.Tick += RateCommandAckTimer_Tick;
            _rateReadbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _rateReadbackTimer.Tick += RateReadbackTimer_Tick;

            AvailablePorts = new ObservableCollection<string> { SimulatorPort };
            foreach (string port in SerialPort.GetPortNames())
                AvailablePorts.Add(port);
            Sections = new ObservableCollection<Brush>();
            SectionRateDisplays = new ObservableCollection<string>();
            ShapefileFields = new ObservableCollection<string>();

            for (int i = 0; i < 8; i++)
            {
                Sections.Add(Brushes.Gray);
                SectionRateDisplays.Add("—");
            }

            // Initialize display values
            Speed = 0;
            Width = 0;
            _currentSectionMask = 0;
            _activeSectionsLast = 0;

            // Load persisted machine configuration (updated whenever AgOpenGPS
            // broadcasts the section dimensions PGN 0xEB)
            _secNum = Properties.Settings.Default.SecNum;
            _secWidth = Properties.Settings.Default.SecWidth;
            _sectionWidthsCm = ParseSavedSectionWidths(Properties.Settings.Default.SectionWidthsCm, _secNum, _secWidth);
            _toolbarOffsetMeters = Properties.Settings.Default.ToolbarOffsetMeters;
            ApplyConfig(showWarning: false);

            // Load persisted COM port
            SelectedPort = Properties.Settings.Default.ComPort;

            // Setup refresh command
            RefreshPortsCommand = new RelayCommand(RefreshPorts);
            LoadShapefileCommand = new RelayCommand(LoadShapefile);

            StartUdp();

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
        }

        private void LoadShapefile()
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Open shapefile",
                Filter = "ESRI Shapefile (*.shp)|*.shp",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                ShapefileData data = ShapefileReader.Read(dialog.FileName);
                _isLoadingShapefile = true;
                ShapefileFeatures = data.Features;
                IsShapefileGeographic = data.IsGeographic;
                ShapefileFields.Clear();
                foreach (string field in data.Fields)
                    ShapefileFields.Add(field);

                SelectedShapefileField = data.Fields.FirstOrDefault(field =>
                    data.Features.Any(feature =>
                        feature.Attributes.TryGetValue(field, out string? value) &&
                        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)));
                ShapefileName = System.IO.Path.GetFileName(dialog.FileName);
                ShapefileLoaded = true;
                string fieldStatus = data.Fields.Count == 0
                    ? $"{data.Features.Count} features loaded. No matching .dbf file found."
                    : $"{data.Features.Count} features loaded; {data.Fields.Count} DBF fields available.";
                ShapefileStatus = $"{fieldStatus} {data.ProjectionSummary}";
                _isLoadingShapefile = false;
                UpdateApplicationRate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _isLoadingShapefile = false;
                ShapefileStatus = $"Could not load shapefile: {ex.Message}";
                MessageBox.Show(
                    ShapefileStatus,
                    "AOG Bogballe Bridge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RefreshPorts()
        {
            string? currentPort = SelectedPort;

            AvailablePorts.Clear();
            AvailablePorts.Add(SimulatorPort);

            foreach (string port in SerialPort.GetPortNames())
            {
                AvailablePorts.Add(port);
            }

            // Try to restore previous selection if it still exists
            if (currentPort != null && AvailablePorts.Contains(currentPort))
            {
                SelectedPort = currentPort;
            }
            else
            {
                SelectedPort = null;
            }
        }

        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            bool connected = (DateTime.Now - _lastUdp).TotalSeconds < 1.5;
            DateTime now = DateTime.UtcNow;
            bool gpsConnected = _lastGpsPacket != default && (now - _lastGpsPacket).TotalSeconds < 3;
            if (_gpsOutPosition.HasValue && now - _lastGpsOutPositionAt > TimeSpan.FromSeconds(3))
            {
                _gpsOutPosition = null;
                _toolHeadingValid = false;
                OnPropertyChanged(nameof(ToolHeadingStatus));
                OnPropertyChanged(nameof(ToolbarPositionValid));
                UpdateApplicationRate();
            }

            if (GpsConnected && !gpsConnected)
            {
                GpsPositionValid = false;
                GpsStatus = "PANDA data timed out.";
                UpdateApplicationRate();
            }
            GpsConnected = gpsConnected;

            // Comms lost: stop spreading rather than holding the last command
            // (matches the Python default CommsLostBehaviour = 0)
            if (UdpConnected && !connected)
            {
                Speed = 0;
                _currentSectionMask = 0;
                UpdateSections(0);
                SendSpeed();
                SendEnable();
                SendSections();
            }

            UdpConnected = connected;
        }

        private void ParseGpsPacket(string packet)
        {
            string line = packet.Trim();
            if (!line.StartsWith("$PANDA,", StringComparison.OrdinalIgnoreCase))
                return;

            _lastGpsPacket = DateTime.UtcNow;
            GpsConnected = true;

            string[] fields = line.Split(',');
            if (fields.Length < 16 || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fixQuality) ||
                fixQuality is < 0 or > 8)
            {
                GpsPositionValid = false;
                GpsStatus = "Invalid PANDA packet: missing or invalid fix quality.";
                Debug.WriteLine($"Invalid PANDA packet: {line}");
                UpdateApplicationRate();
                return;
            }

            GpsFixQuality = fixQuality;
            if (fixQuality == 0)
            {
                GpsPositionValid = false;
                GpsStatus = "PANDA reports no GPS fix.";
                UpdateApplicationRate();
                return;
            }

            if (!TryParseNmeaCoordinate(fields[2], fields[3], isLatitude: true, out double latitude) ||
                !TryParseNmeaCoordinate(fields[4], fields[5], isLatitude: false, out double longitude))
            {
                GpsPositionValid = false;
                GpsStatus = "Invalid PANDA packet: latitude or longitude is malformed.";
                Debug.WriteLine($"Invalid PANDA coordinates: {line}");
                UpdateApplicationRate();
                return;
            }

            GpsLatitude = latitude;
            GpsLongitude = longitude;
            GpsSatellites = int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int satellites)
                ? Math.Max(0, satellites)
                : 0;
            GpsPositionValid = true;
            GpsStatus = $"Fix quality {fixQuality}; {GpsSatellites} satellites.";
            UpdateApplicationRate();
        }

        private static bool TryParseNmeaCoordinate(string coordinate, string hemisphere, bool isLatitude, out double decimalDegrees)
        {
            decimalDegrees = 0;
            if (!double.TryParse(coordinate, NumberStyles.Float, CultureInfo.InvariantCulture, out double nmeaValue) ||
                !double.IsFinite(nmeaValue) || nmeaValue < 0)
                return false;

            string direction = hemisphere.Trim().ToUpperInvariant();
            if (isLatitude ? direction is not ("N" or "S") : direction is not ("E" or "W"))
                return false;

            double degrees = Math.Floor(nmeaValue / 100);
            double minutes = nmeaValue - degrees * 100;
            double maximumDegrees = isLatitude ? 90 : 180;
            if (minutes >= 60 || degrees > maximumDegrees || (degrees == maximumDegrees && minutes > 0))
                return false;

            decimalDegrees = degrees + minutes / 60;
            if (direction is "S" or "W")
                decimalDegrees = -decimalDegrees;
            return true;
        }

        private void StartUdp()
        {
            try
            {
                _udp = new UdpClient();

                _udp.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress,
                    true);

                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 8888));

                BeginReceive();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR starting UDP: {ex.Message}");
            }
        }

        private void BeginReceive()
        {
            try
            {
                _udp?.BeginReceive(UdpCallback, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in BeginReceive: {ex.Message}");
            }
        }

        private void UdpCallback(IAsyncResult ar)
        {
            IPEndPoint? ep = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                byte[] data = _udp!.EndReceive(ar, ref ep);
                // Parse on UI thread to ensure property updates work
                _dispatcher.BeginInvoke(new Action(() => ParseAogPacket(data)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in UdpCallback: {ex.Message}");
            }
            finally
            {
                BeginReceive();
            }
        }

        private void ParseAogPacket(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            string textPacket = Encoding.ASCII.GetString(data).Trim();
            if (textPacket.StartsWith("$PANDA,", StringComparison.OrdinalIgnoreCase))
            {
                ParseGpsPacket(textPacket);
                return;
            }

            _lastUdp = DateTime.Now;
            if (data.Length < 6)
                return;

            byte pgn = data[3];
            LastPgn = $"0x{pgn:X2}";

            // GPS output PGN: longitude and latitude as little-endian doubles.
            if (pgn == 0x64)
            {
                ParseGpsOutPacket(data);
            }
            // Speed PGN (Steer data - same as Python)
            else if (pgn == 0xFE)
            {
                if (data.Length < 7)
                    return;

                ushort spd = BitConverter.ToUInt16(data, 5);
                Speed = Math.Round(spd * 0.1, 1);
                SendSpeed();
                SendEnable();
                SendSections(); // Always send sections with speed updates
            }
            // Machine Data PGN (has section data in byte 11)
            else if (pgn == 0xEF)
            {
                if (data.Length >= 12)
                {
                    byte sectBits = data[11]; // Byte 11 contains sections 1-8
                    _currentSectionMask = sectBits;
                    UpdateSections(sectBits);
                    SendEnable();
                    SendSections(); // Always send sections when they update
                }
            }
            // Section control
            else if (pgn == 0xEC)
            {
                byte mask = data[5];
                _currentSectionMask = mask;
                UpdateSections(mask);
                SendEnable();
                SendSections(); // Always send sections when they update
            }
            // Section dimensions PGN: sixteen per-section widths in centimeters and a count.
            else if (pgn == 0xEB)
            {
                if (data.Length < 38)
                    return;

                int secNum = data[37];
                if (secNum > 16)
                {
                    GpsStatus = $"Invalid machine configuration: {secNum} sections.";
                    return;
                }

                double[] sectionWidthsCm = new double[secNum];
                for (int i = 0; i < secNum; i++)
                    sectionWidthsCm[i] = BitConverter.ToUInt16(data, 5 + i * 2);

                if (secNum == _secNum && sectionWidthsCm.SequenceEqual(_sectionWidthsCm))
                    return;

                _secNum = secNum;
                _sectionWidthsCm = sectionWidthsCm;
                _secWidth = sectionWidthsCm.Length > 0 ? sectionWidthsCm[0] : 0;
                Properties.Settings.Default.SecNum = _secNum;
                Properties.Settings.Default.SecWidth = _secWidth;
                Properties.Settings.Default.SectionWidthsCm = string.Join(",", sectionWidthsCm.Select(width =>
                    width.ToString("0", CultureInfo.InvariantCulture)));
                Properties.Settings.Default.Save();

                ApplyConfig(showWarning: true);
                SendTotalWidth();
                UpdateToolbarPosition();
                UpdateApplicationRate();
            }
            // AgIO Hello PGN
            else if (pgn == 0xC8)
            {
                _aogVersion = data[5];

                if (_aogVersion < 56 && !_versionWarned)
                {
                    _versionWarned = true;
                    MessageBox.Show(
                        "This version of AgOpenGPS is not supported! Some features may not work as intended.\n" +
                        "Consider upgrading to version 5.7 or newer.\n\n" +
                        $"Detected version: {_aogVersion / 10.0:F1}",
                        "AOG-Bogballe Bridge",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            // Width PGN
            else if (pgn == 0xE7)
            {
                if (data.Length < 9)
                    return;

                ushort widthRaw = BitConverter.ToUInt16(data, 7);
                Width = Math.Round(widthRaw * 0.01, 1);
            }
        }

        private void ParseGpsOutPacket(byte[] data)
        {
            _lastGpsPacket = DateTime.UtcNow;
            GpsConnected = true;

            int payloadLength = data[4];
            if (payloadLength is not (16 or 24) || data.Length != payloadLength + 6)
            {
                InvalidateGpsOutPosition();
                GpsPositionValid = false;
                GpsStatus = "Invalid GPSOut packet length.";
                Debug.WriteLine($"Invalid GPSOut packet length: {data.Length} bytes, payload {payloadLength} bytes.");
                UpdateApplicationRate();
                return;
            }

            double longitude = ReadLittleEndianDouble(data, 5);
            double latitude = ReadLittleEndianDouble(data, 13);
            if (!double.IsFinite(longitude) || longitude is < -180 or > 180 ||
                !double.IsFinite(latitude) || latitude is < -90 or > 90)
            {
                InvalidateGpsOutPosition();
                GpsPositionValid = false;
                GpsStatus = "Invalid GPSOut coordinates.";
                Debug.WriteLine($"GPSOut coordinates out of range: longitude {longitude}, latitude {latitude}.");
                UpdateApplicationRate();
                return;
            }

            DateTime receivedAt = DateTime.UtcNow;
            Point gpsPosition = new(longitude, latitude);
            _gpsOutPosition = gpsPosition;
            _lastGpsOutPositionAt = receivedAt;
            GpsLongitude = longitude;
            GpsLatitude = latitude;
            GpsPositionValid = true;
            GpsStatus = "GPS position received";
            UpdateToolHeading(data, gpsPosition, receivedAt);
            UpdateToolbarPosition();
            UpdateApplicationRate();
        }

        private void InvalidateGpsOutPosition()
        {
            _gpsOutPosition = null;
            _toolHeadingValid = false;
            OnPropertyChanged(nameof(ToolHeadingStatus));
            OnPropertyChanged(nameof(ToolbarPositionValid));
        }

        private void UpdateToolHeading(byte[] data, Point currentPosition, DateTime receivedAt)
        {
            bool headingFromAog = false;
            if (data[4] == 24)
            {
                double fixToFixHeading = ReadLittleEndianDouble(data, 21);
                if (IsValidHeading(fixToFixHeading))
                {
                    _toolHeadingDegrees = fixToFixHeading;
                    _toolHeadingUpdatedAt = receivedAt;
                    _toolHeadingValid = true;
                    _toolHeadingSource = "AOG Fix2Fix";
                    headingFromAog = true;
                }
            }

            if (!headingFromAog && _previousGpsPosition.HasValue && Speed >= 1.0)
            {
                double elapsedSeconds = (receivedAt - _previousGpsPositionAt).TotalSeconds;
                double distanceMeters = CalculateDistanceMeters(_previousGpsPosition.Value, currentPosition);
                if (elapsedSeconds is > 0.05 and <= 3 && distanceMeters >= 0.5)
                {
                    _toolHeadingDegrees = CalculateBearingDegrees(_previousGpsPosition.Value, currentPosition);
                    _toolHeadingUpdatedAt = receivedAt;
                    _toolHeadingValid = true;
                    _toolHeadingSource = "GPS course";
                }
            }

            _previousGpsPosition = currentPosition;
            _previousGpsPositionAt = receivedAt;
            if (_toolHeadingValid && receivedAt - _toolHeadingUpdatedAt > TimeSpan.FromSeconds(3))
                _toolHeadingValid = false;

            OnPropertyChanged(nameof(ToolHeadingDegrees));
            OnPropertyChanged(nameof(ToolHeadingStatus));
            OnPropertyChanged(nameof(ToolbarPositionValid));
        }

        private void UpdateToolbarPosition()
        {
            if (!GpsPositionValid || !_toolHeadingValid ||
                DateTime.UtcNow - _toolHeadingUpdatedAt > TimeSpan.FromSeconds(3))
            {
                OnPropertyChanged(nameof(ToolbarPositionValid));
                return;
            }

            Point gpsPoint = _gpsOutPosition!.Value;
            Point toolbarPoint = MovePointByBearing(gpsPoint, _toolHeadingDegrees + 180, _toolbarOffsetMeters);
            _toolbarLatitude = toolbarPoint.Y;
            _toolbarLongitude = toolbarPoint.X;
            OnPropertyChanged(nameof(ToolbarLatitude));
            OnPropertyChanged(nameof(ToolbarLongitude));
            OnPropertyChanged(nameof(ToolbarPositionValid));
        }

        private static bool IsValidHeading(double heading) =>
            double.IsFinite(heading) && heading is >= 0 and < 360;

        private static double CalculateBearingDegrees(Point from, Point to)
        {
            double latitude1 = DegreesToRadians(from.Y);
            double latitude2 = DegreesToRadians(to.Y);
            double longitudeDelta = DegreesToRadians(to.X - from.X);
            double y = Math.Sin(longitudeDelta) * Math.Cos(latitude2);
            double x = Math.Cos(latitude1) * Math.Sin(latitude2) -
                       Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDelta);
            return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        }

        private static double CalculateDistanceMeters(Point from, Point to)
        {
            double latitude1 = DegreesToRadians(from.Y);
            double latitude2 = DegreesToRadians(to.Y);
            double latitudeDelta = latitude2 - latitude1;
            double longitudeDelta = DegreesToRadians(to.X - from.X);
            double haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                               Math.Cos(latitude1) * Math.Cos(latitude2) * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
            return 2 * 6_371_000 * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
        }

        private static Point MovePointByBearing(Point position, double bearingDegrees, double distanceMeters)
        {
            double bearing = DegreesToRadians(bearingDegrees);
            double angularDistance = distanceMeters / 6_371_000;
            double latitude1 = DegreesToRadians(position.Y);
            double longitude1 = DegreesToRadians(position.X);
            double latitude2 = Math.Asin(
                Math.Sin(latitude1) * Math.Cos(angularDistance) +
                Math.Cos(latitude1) * Math.Sin(angularDistance) * Math.Cos(bearing));
            double longitude2 = longitude1 + Math.Atan2(
                Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitude1),
                Math.Cos(angularDistance) - Math.Sin(latitude1) * Math.Sin(latitude2));
            return new Point(longitude2 * 180 / Math.PI, latitude2 * 180 / Math.PI);
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

        private void UpdateApplicationRate()
        {
            if (!ShapefileLoaded)
            {
                _rateZoneValid = false;
                SetSectionRateDisplays(null);
                CurrentApplicationRate = "Shapefile rate control off; using normal spreader control.";
                return;
            }

            string? rateText = null;
            string status;
            double[]? sectionRates = null;

            if (!GpsPositionValid)
            {
                status = "No valid GPS fix; spreading disabled.";
            }
            else if (!IsShapefileGeographic)
            {
                status = "Rate lookup requires a geographic shapefile; spreading disabled.";
            }
            else if (string.IsNullOrWhiteSpace(SelectedShapefileField))
            {
                status = "Select a DBF rate field; spreading disabled.";
            }
            else if (!ToolbarPositionValid)
            {
                status = "Waiting for valid toolbar width and heading; spreading disabled.";
            }
            else if (!TryCalculateSwathRates(out double averageRate, out sectionRates, out string rateFailure))
            {
                status = rateFailure;
            }
            else
            {
                rateText = Math.Round(averageRate, 2, MidpointRounding.AwayFromZero)
                    .ToString("0.##", CultureInfo.InvariantCulture);
                status = $"Full toolbar coverage; average application rate {rateText} kg/ha.";
            }

            SetSectionRateDisplays(rateText == null ? null : sectionRates);
            bool rateChanged = !string.Equals(_currentApplicationRate, rateText, StringComparison.Ordinal);
            _rateZoneValid = rateText != null;
            CurrentApplicationRate = rateText ?? status;
            GpsStatus = status;

            if (rateText is not null)
            {
                if (rateChanged || _lastSentApplicationRate == null)
                {
                    SendAppRatWithFallback(rateText, TryBuildUniqLegacyRateCommand(rateText));
                    _lastSentApplicationRate = rateText;
                }
            }
            else
            {
                _lastSentApplicationRate = null;
            }

            SendEnable();
        }

        private void SetSectionRateDisplays(IReadOnlyList<double>? rates)
        {
            for (int i = 0; i < SectionRateDisplays.Count; i++)
            {
                string display = rates != null && i < rates.Count
                    ? $"{Math.Round(rates[i], 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture)} kg/ha"
                    : "—";
                if (!string.Equals(SectionRateDisplays[i], display, StringComparison.Ordinal))
                    SectionRateDisplays[i] = display;
            }
        }

        private bool TryCalculateSwathRates(out double averageRate, out double[]? sectionRates, out string failure)
        {
            averageRate = 0;
            sectionRates = null;
            double totalWidth = MachineWidthMeters;
            if (!double.IsFinite(totalWidth) || totalWidth <= 0 || !_validConfig || !ToolbarPositionValid ||
                string.IsNullOrWhiteSpace(SelectedShapefileField))
            {
                failure = "Toolbar geometry is invalid; spreading disabled.";
                return false;
            }

            Point toolbarCenter = new(_toolbarLongitude, _toolbarLatitude);
            double halfWidth = totalWidth / 2;
            double latitudeRadius = halfWidth / 6_371_000 * 180 / Math.PI + 1e-7;
            double longitudeCosine = Math.Max(Math.Abs(Math.Cos(DegreesToRadians(toolbarCenter.Y))), 1e-6);
            double longitudeRadius = halfWidth / (6_371_000 * longitudeCosine) * 180 / Math.PI + 1e-7;
            double minLongitude = toolbarCenter.X - longitudeRadius;
            double maxLongitude = toolbarCenter.X + longitudeRadius;
            double minLatitude = toolbarCenter.Y - latitudeRadius;
            double maxLatitude = toolbarCenter.Y + latitudeRadius;
            bool crossesAntimeridian = minLongitude < -180 || maxLongitude > 180;
            ShapefileFeature[] nearbyFeatures = ShapefileFeatures.Where(feature =>
                feature.GeometryType == ShapefileGeometryType.Polygon &&
                (crossesAntimeridian || (feature.MaxX >= minLongitude && feature.MinX <= maxLongitude)) &&
                feature.MaxY >= minLatitude && feature.MinY <= maxLatitude).ToArray();

            List<double> boundaries = new() { -halfWidth, halfWidth };
            double sectionBoundary = -halfWidth;
            foreach (double sectionWidth in _sectionWidthsMeters)
            {
                sectionBoundary += sectionWidth;
                if (sectionBoundary < halfWidth - 0.0001)
                    boundaries.Add(sectionBoundary);
            }

            AddSwathPolygonIntersections(
                toolbarCenter,
                _toolHeadingDegrees + 90,
                halfWidth,
                nearbyFeatures,
                boundaries);
            boundaries.Sort();

            double weightedRateSum = 0;
            double coveredWidth = 0;
            double[] sectionWeightedRates = new double[_secNum];
            double[] sectionCoveredWidths = new double[_secNum];
            double[] sectionEnds = new double[_secNum];
            double nextSectionEnd = -halfWidth;
            for (int i = 0; i < _secNum; i++)
            {
                nextSectionEnd += _sectionWidthsMeters[i];
                sectionEnds[i] = nextSectionEnd;
            }

            int sectionIndex = 0;
            double previousBoundary = boundaries[0];
            for (int i = 1; i < boundaries.Count; i++)
            {
                double boundary = boundaries[i];
                double segmentWidth = boundary - previousBoundary;
                if (segmentWidth <= 0.0001)
                    continue;

                double crossTrackDistance = (previousBoundary + boundary) / 2;
                while (sectionIndex < _secNum - 1 && crossTrackDistance >= sectionEnds[sectionIndex])
                    sectionIndex++;

                Point samplePoint = MovePointByBearing(toolbarCenter, _toolHeadingDegrees + 90, crossTrackDistance);
                ShapefileFeature? feature = nearbyFeatures.FirstOrDefault(candidate => candidate.Contains(samplePoint));
                if (feature == null)
                {
                    failure = "Toolbar crosses an uncovered area; spreading disabled.";
                    return false;
                }

                if (!feature.Attributes.TryGetValue(SelectedShapefileField!, out string? value) ||
                    !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) ||
                    !double.IsFinite(rate) || rate < 0)
                {
                    failure = $"Rate field '{SelectedShapefileField}' is missing or invalid across the toolbar; spreading disabled.";
                    return false;
                }

                weightedRateSum += rate * segmentWidth;
                sectionWeightedRates[sectionIndex] += rate * segmentWidth;
                if (!double.IsFinite(weightedRateSum) || !double.IsFinite(sectionWeightedRates[sectionIndex]))
                {
                    failure = "Calculated toolbar rate is outside the supported numeric range; spreading disabled.";
                    return false;
                }

                coveredWidth += segmentWidth;
                sectionCoveredWidths[sectionIndex] += segmentWidth;
                previousBoundary = boundary;
            }

            if (Math.Abs(coveredWidth - totalWidth) > 0.001)
            {
                failure = "Toolbar coverage could not be verified; spreading disabled.";
                return false;
            }

            sectionRates = new double[_secNum];
            for (int i = 0; i < _secNum; i++)
            {
                if (Math.Abs(sectionCoveredWidths[i] - _sectionWidthsMeters[i]) > 0.001)
                {
                    failure = "Full coverage could not be verified for every section; spreading disabled.";
                    sectionRates = null;
                    return false;
                }

                sectionRates[i] = sectionWeightedRates[i] / _sectionWidthsMeters[i];
                if (!double.IsFinite(sectionRates[i]))
                {
                    failure = "Calculated section rate is outside the supported numeric range; spreading disabled.";
                    sectionRates = null;
                    return false;
                }
            }

            averageRate = weightedRateSum / totalWidth;
            if (!double.IsFinite(averageRate))
            {
                failure = "Calculated toolbar rate is outside the supported numeric range; spreading disabled.";
                sectionRates = null;
                return false;
            }

            failure = string.Empty;
            return true;
        }

        private void AddSwathPolygonIntersections(
            Point center,
            double crossTrackBearing,
            double halfWidth,
            IReadOnlyList<ShapefileFeature> nearbyFeatures,
            List<double> boundaries)
        {
            double bearingRadians = DegreesToRadians(crossTrackBearing);
            double directionX = Math.Sin(bearingRadians);
            double directionY = Math.Cos(bearingRadians);
            double normalX = -directionY;
            double normalY = directionX;
            double latitudeScale = Math.Cos(DegreesToRadians(center.Y));

            Point ToLocalMeters(Point point)
            {
                double longitudeDelta = point.X - center.X;
                if (longitudeDelta > 180)
                    longitudeDelta -= 360;
                else if (longitudeDelta < -180)
                    longitudeDelta += 360;

                return new Point(
                    6_371_000 * DegreesToRadians(longitudeDelta) * latitudeScale,
                    6_371_000 * DegreesToRadians(point.Y - center.Y));
            }

            foreach (ShapefileFeature feature in nearbyFeatures)
            {
                foreach (IReadOnlyList<Point> ring in feature.Parts)
                {
                    for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    {
                        Point a = ToLocalMeters(ring[j]);
                        Point b = ToLocalMeters(ring[i]);
                        double sideA = a.X * normalX + a.Y * normalY;
                        double sideB = b.X * normalX + b.Y * normalY;
                        if (Math.Abs(sideA) <= 1e-8 && Math.Abs(sideB) <= 1e-8)
                        {
                            AddIntersection(a);
                            AddIntersection(b);
                            continue;
                        }

                        if ((sideA < 0 && sideB > 0) || (sideA > 0 && sideB < 0) || Math.Abs(sideA) <= 1e-8 || Math.Abs(sideB) <= 1e-8)
                        {
                            double fraction = Math.Abs(sideA) <= 1e-8 ? 0 :
                                Math.Abs(sideB) <= 1e-8 ? 1 : sideA / (sideA - sideB);
                            AddIntersection(new Point(
                                a.X + (b.X - a.X) * fraction,
                                a.Y + (b.Y - a.Y) * fraction));
                        }

                        void AddIntersection(Point point)
                        {
                            double distance = point.X * directionX + point.Y * directionY;
                            if (distance > -halfWidth && distance < halfWidth)
                                boundaries.Add(distance);
                        }
                    }
                }
            }
        }

        private static double ReadLittleEndianDouble(byte[] data, int offset) =>
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, sizeof(double))));

        private void ApplyConfig(bool showWarning)
        {
            bool widthsValid = _sectionWidthsCm.Length == _secNum &&
                                _sectionWidthsCm.All(width => double.IsFinite(width) && width > 0);
            _validConfig = _secNum is 2 or 4 or 8 && widthsValid;
            _sectionWidthsMeters = _sectionWidthsCm.Select(width => width / 100.0).ToArray();
            OnPropertyChanged(nameof(SectionWidthsMeters));
            OnPropertyChanged(nameof(MachineWidthMeters));
            OnPropertyChanged(nameof(ToolbarPositionValid));
            UpdateSections(_currentSectionMask);

            Width = widthsValid ? Math.Round(_sectionWidthsCm.Sum() / 100.0, 1) : 0;
            if (_validConfig)
            {
                ConfigText = $"{_secNum} sections; total width {Width:F1} m";
            }
            else if (_secNum == 0)
            {
                ConfigText = "Waiting for machine configuration from AgOpenGPS...";
            }
            else
            {
                ConfigText = widthsValid
                    ? $"Unsupported: {_secNum} sections; total width {Width:F1} m"
                    : "Machine section widths are incomplete or invalid.";

                if (showWarning)
                {
                    MessageBox.Show(
                        "Machine configuration not supported.\nPlease use 2, 4 or 8 sections with a valid width for each section.\n\n" +
                        ConfigText,
                        "AOG-Bogballe Bridge",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }

        private static double[] ParseSavedSectionWidths(string? savedWidths, int sectionCount, double legacyWidth)
        {
            if (sectionCount <= 0 || sectionCount > 16)
                return Array.Empty<double>();

            string[] values = (savedWidths ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == sectionCount && values.All(value =>
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && width > 0))
                return values.Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

            return legacyWidth > 0
                ? Enumerable.Repeat(legacyWidth, sectionCount).ToArray()
                : Array.Empty<double>();
        }

        private void UpdateSections(byte mask)
        {
            int shown = _validConfig ? _secNum : 8;
            for (int i = 0; i < 8; i++)
            {
                if (i >= shown)
                {
                    Sections[i] = Brushes.Gray; // not part of the configured machine
                    continue;
                }

                bool active = (mask & (1 << i)) != 0;
                Sections[i] = active ? Brushes.LimeGreen : Brushes.DarkGray;
            }
        }

        private int CountActiveSections()
        {
            int count = 0;
            for (int i = 0; i < _secNum; i++)
            {
                if ((_currentSectionMask & (1 << i)) != 0)
                    count++;
            }
            return count;
        }

        private void ConnectSerial()
        {
            try
            {
                _rateCommandAckTimer.Stop();
                _rateReadbackTimer.Stop();
                _appRatProtocolConfirmed = false;
                _legacyRateProtocolConfirmed = false;
                _pendingFallbackCommand = null;
                ResetCalibratorRateReadbacks();
                lock (_serialReceiveLock)
                    _serialReceiveBuffer = string.Empty;
                if (_serial != null)
                {
                    _serial.DataReceived -= Serial_DataReceived;
                    _serial.Close();
                    _serial.Dispose();
                    _serial = null;
                }

                if (IsSimulatorMode)
                {
                    SerialConnected = false;
                    _simulatorOutput = "No Bogballe command received yet.";
                    OnPropertyChanged(nameof(SimulatorStatus));
                    _lastSentApplicationRate = null;
                    _activeSectionsLast = -1;
                    _spreaderEnabledLast = !_rateZoneValid;
                    UpdateApplicationRate();
                    return;
                }

                if (string.IsNullOrWhiteSpace(SelectedPort))
                {
                    SerialConnected = false;
                    return;
                }

                // Write timeout stops a stalled adapter from freezing the UI thread
                _serial = new SerialPort(SelectedPort, 9600) { WriteTimeout = 500 };
                _serial.Open();
                _serial.DataReceived += Serial_DataReceived;
                SerialConnected = true;
                _rateReadbackTimer.Start();
                _lastSentApplicationRate = null;

                // Force enable/width to be resent to the (re)connected spreader
                _activeSectionsLast = -1;
                _spreaderEnabledLast = !_rateZoneValid;
                UpdateApplicationRate();
            }
            catch
            {
                SerialConnected = false;
            }
        }

        private bool SendToSpreader(string message)
        {
            if (IsSimulatorMode)
            {
                if (_lastUdp == default || (DateTime.Now - _lastUdp).TotalSeconds >= 1.5)
                    return false;

                string checksum = CalculateAsciiChecksum(message);
                string fullMessage = $"{{{message}{checksum}}}";
                _simulatorOutput = FormatCommandForDisplay(message, fullMessage);
                OnPropertyChanged(nameof(SimulatorStatus));
                return true;
            }

            if (!SerialConnected || _serial == null)
                return false;

            try
            {
                // Frame: {message checksum}
                string checksum = CalculateAsciiChecksum(message);
                string fullMessage = $"{{{message}{checksum}}}";

                byte[] msg = Encoding.ASCII.GetBytes(fullMessage);
                _serial.Write(msg, 0, msg.Length);
                return true;
            }
            catch (Exception ex)
            {
                SerialConnected = false;
                Debug.WriteLine($"ERROR sending '{message}': {ex.Message}");
                return false;
            }
        }

        private void SendAppRatWithFallback(string rate, string? fallbackCommand)
        {
            if (_legacyRateProtocolConfirmed && fallbackCommand != null)
            {
                bool legacyWritten = SendToSpreader(fallbackCommand);
                SetLegacyRateFallbackUsed(legacyWritten && !IsSimulatorMode);
                if (!legacyWritten)
                    RateProtocolStatus = "UNIQ legacy rate command could not be sent.";
                return;
            }

            bool appRatSent = SendToSpreader($"S:AppRat:{rate}:");
            if (!appRatSent)
            {
                if (!_rateCommandAckTimer.IsEnabled)
                {
                    _pendingFallbackCommand = null;
                }
                RateProtocolStatus = "Application-rate command could not be sent.";
                return;
            }

            if (fallbackCommand == null)
            {
                _pendingFallbackCommand = null;
                RateProtocolStatus = "Rate exceeds the UNIQ legacy fallback limit (99999 kg/ha).";
                return;
            }

            if (_appRatProtocolConfirmed)
                return;

            // Refresh the fallback target without restarting the first acknowledgement window.
            _pendingFallbackCommand = fallbackCommand;
            if (!_rateCommandAckTimer.IsEnabled && (IsSimulatorMode || SerialConnected))
            {
                RateProtocolStatus = IsSimulatorMode
                    ? "Simulator recorded AppRat; waiting for a real calibrator response."
                    : "Waiting for a Bogballe response; UNIQ fallback follows if needed.";
                _rateCommandAckTimer.Start();
            }
        }

        private void RateCommandAckTimer_Tick(object? sender, EventArgs e)
        {
            _rateCommandAckTimer.Stop();
            string? fallbackCommand = _pendingFallbackCommand;
            _pendingFallbackCommand = null;

            if (fallbackCommand != null && (IsSimulatorMode || SerialConnected))
            {
                _legacyRateProtocolConfirmed = true;
                bool fallbackWritten = SendToSpreader(fallbackCommand);
                SetLegacyRateFallbackUsed(fallbackWritten && !IsSimulatorMode);
                RateProtocolStatus = IsSimulatorMode
                    ? "Simulator recorded the UNIQ legacy fallback; no calibrator response is simulated."
                    : fallbackWritten
                        ? "UNIQ legacy fallback used."
                        : "UNIQ legacy fallback could not be sent.";
            }
        }

        private void RateReadbackTimer_Tick(object? sender, EventArgs e)
        {
            if (!SerialConnected || _serial == null || _rateCommandAckTimer.IsEnabled)
                return;

            SendToSpreader("rDC");
        }

        private void Serial_DataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            if (sender is not SerialPort serial)
                return;

            try
            {
                int availableBytes = serial.BytesToRead;
                if (availableBytes == 0)
                    return;

                byte[] bytes = new byte[availableBytes];
                int bytesRead = serial.Read(bytes, 0, bytes.Length);
                if (bytesRead == 0)
                    return;

                string incoming = Encoding.Latin1.GetString(bytes, 0, bytesRead);
                List<string> frames = new();
                lock (_serialReceiveLock)
                {
                    _serialReceiveBuffer += incoming;
                    while (true)
                    {
                        int start = _serialReceiveBuffer.IndexOf('{');
                        if (start < 0)
                        {
                            _serialReceiveBuffer = string.Empty;
                            break;
                        }

                        if (start > 0)
                            _serialReceiveBuffer = _serialReceiveBuffer[start..];

                        int end = _serialReceiveBuffer.IndexOf('}', 1);
                        if (end < 0)
                            break;

                        frames.Add(_serialReceiveBuffer[..(end + 1)]);
                        _serialReceiveBuffer = _serialReceiveBuffer[(end + 1)..];
                    }
                }

                if (frames.Count > 0)
                    _dispatcher.BeginInvoke(new Action(() => ProcessSerialFrames(frames)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR reading Bogballe serial response: {ex.Message}");
            }
        }

        private void ProcessSerialFrames(IEnumerable<string> frames)
        {
            foreach (string frame in frames)
            {
                if (frame.Length < 4)
                    continue;

                // Bogballe XORs the payload bytes, excluding the opening brace, checksum, and closing brace.
                string payload = frame[1..^2];
                if (CalculateAsciiChecksum(payload)[0] != frame[^2])
                {
                    Debug.WriteLine("Ignored Bogballe response with an invalid checksum.");
                    continue;
                }

                if (TryProcessLegacyRateResponse(payload))
                    continue;

                string[] fields = payload.Split(':');
                if (fields.Length < 4 || fields[0] != "A" || fields[1] != "AppRat" ||
                    !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double acknowledgedRate) ||
                    !double.IsFinite(acknowledgedRate))
                    continue;

                CalibratorSetRateDisplay = FormatCalibratorRate(acknowledgedRate);
                _appRatProtocolConfirmed = true;
                _legacyRateProtocolConfirmed = false;
                _rateCommandAckTimer.Stop();
                _pendingFallbackCommand = null;
                SetLegacyRateFallbackUsed(false);
                RateProtocolStatus = $"Bogballe acknowledged {fields[2]} kg/ha; legacy fallback not used.";
            }
        }

        private bool TryProcessLegacyRateResponse(string payload)
        {
            if (payload.Length < 3 ||
                !double.TryParse(payload[2..], NumberStyles.None, CultureInfo.InvariantCulture, out double reportedRate))
                return false;

            string command = payload[..2];
            if (command is "WD" or "wD" or "AD" or "aD")
                CalibratorSetRateDisplay = FormatCalibratorRate(reportedRate);
            else
                return false;

            if (command is "AD" or "aD")
            {
                _legacyRateProtocolConfirmed = true;
                _appRatProtocolConfirmed = false;
                _rateCommandAckTimer.Stop();
                _pendingFallbackCommand = null;
                SetLegacyRateFallbackUsed(true);
                RateProtocolStatus = $"Calibrator set rate: {FormatCalibratorRate(reportedRate)} (UNIQ legacy).";
            }

            return true;
        }

        private void ResetCalibratorRateReadbacks()
        {
            CalibratorSetRateDisplay = "—";
            SetLegacyRateFallbackUsed(false);
        }

        private void SetLegacyRateFallbackUsed(bool used)
        {
            if (_legacyRateFallbackUsed == used)
                return;

            _legacyRateFallbackUsed = used;
            OnPropertyChanged(nameof(LegacyRateFallbackNote));
        }

        private static string FormatCalibratorRate(double rate) =>
            $"{rate.ToString("0.##", CultureInfo.InvariantCulture)} kg/ha";

        private static string? TryBuildUniqLegacyRateCommand(string rateText)
        {
            // UNIQ legacy uses SD for up to four digits and lowercase sD for five-digit rates.
            if (!double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericRate) ||
                !double.IsFinite(numericRate))
                return null;

            double roundedRate = Math.Round(numericRate, MidpointRounding.AwayFromZero);
            if (roundedRate is < 0 or > 99999)
                return null;

            int rate = (int)roundedRate;
            return rate <= 9999
                ? $"SD{rate.ToString("D3", CultureInfo.InvariantCulture)}"
                : $"sD{rate.ToString("D5", CultureInfo.InvariantCulture)}";
        }

        private static string FormatCommandForDisplay(string message, string fullMessage)
        {
            byte checksum = (byte)fullMessage[fullMessage.Length - 2];
            return $"{{{message}<checksum 0x{checksum:X2}>}}";
        }

        private void SendSpeed()
        {
            // Format: {S:SpdKmh:0.0:checksum}
            SendToSpreader($"S:SpdKmh:{Speed.ToString("F1", CultureInfo.InvariantCulture)}:");
        }

        private void SendSections()
        {
            if (!_validConfig)
                return;

            // Bogballe always expects 8 section states, so 2- and 4-section
            // configurations are expanded (each section repeated 8/secNum times).
            // Format: {S:SOrlBs:0:0:0:0:0:0:0:0:checksum}
            StringBuilder message = new StringBuilder("S:SOrlBs:");
            int repeats = 8 / _secNum;
            for (int i = 0; i < _secNum; i++)
            {
                char state = (_currentSectionMask & (1 << i)) != 0 ? '1' : '0';
                for (int r = 0; r < repeats; r++)
                {
                    message.Append(state);
                    message.Append(':');
                }
            }

            SendToSpreader(message.ToString());
        }

        private void SendEnable()
        {
            int activeSections = _validConfig ? CountActiveSections() : 0;
            int activeSectionMask = _validConfig ? _currentSectionMask : 0;
            bool rateAllowsSpreading = !ShapefileLoaded || _rateZoneValid;
            bool shouldEnable = _validConfig && rateAllowsSpreading && activeSections > 0;
            if (activeSections == _activeSectionsLast && activeSectionMask == _activeSectionMaskLast &&
                shouldEnable == _spreaderEnabledLast)
                return;

            _activeSectionsLast = activeSections;
            _activeSectionMaskLast = activeSectionMask;
            _spreaderEnabledLast = shouldEnable;

            // Format: {S:SOrlSE:1:checksum} (1 = spreading enabled, 0 = disabled)
            SendToSpreader(shouldEnable ? "S:SOrlSE:1:" : "S:SOrlSE:0:");
            if (_validConfig)
                SendActiveWidth();
        }

        private void SendActiveWidth()
        {
            // Format: {S:SOrlWt:1:width_m:checksum}
            double activeWidth = 0;
            for (int i = 0; i < _secNum; i++)
            {
                if ((_currentSectionMask & (1 << i)) != 0)
                    activeWidth += _sectionWidthsCm[i] / 100.0;
            }

            activeWidth = Math.Round(activeWidth, 1);
            SendToSpreader($"S:SOrlWt:1:{activeWidth.ToString("F1", CultureInfo.InvariantCulture)}:");
        }

        private void SendTotalWidth()
        {
            // Format: {S:SprdWt:width_m checksum}
            // Built for protocol completeness but not transmitted, matching the
            // original Python implementation where this write was disabled.
            double totalWidth = Math.Round(MachineWidthMeters, 1);
            Debug.WriteLine($"Total spread width: {{S:SprdWt:{totalWidth.ToString("F1", CultureInfo.InvariantCulture)}}} (not transmitted)");
        }

        private static string CalculateAsciiChecksum(string data)
        {
            byte cs = 0;
            foreach (char c in data)
            {
                cs ^= (byte)c;
            }

            // Avoid special characters (same as Python logic)
            if (cs == 0x00 || cs == 0x7B || cs == 0x7D)
                cs = 0x55;

            return ((char)cs).ToString();
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // Simple RelayCommand implementation
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute == null || _canExecute();
        }

        public void Execute(object? parameter)
        {
            _execute();
        }
    }
}