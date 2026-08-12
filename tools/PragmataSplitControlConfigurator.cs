using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("PRAGMATA Split Control")]
[assembly: AssemblyDescription("Controller assignment and live input tester for PRAGMATA Split Control")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch
        {
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ConfiguratorForm());
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

internal sealed class ConfiguratorForm : Form
{
    private static readonly Color WindowColor = Color.FromArgb(23, 29, 37);
    private static readonly Color PanelColor = Color.FromArgb(32, 42, 52);
    private static readonly Color TextColor = Color.FromArgb(220, 226, 230);
    private static readonly Color MutedColor = Color.FromArgb(143, 160, 175);
    private static readonly Color AccentColor = Color.FromArgb(102, 192, 244);
    private static readonly Color PlayerTwoColor = Color.FromArgb(194, 130, 255);
    private static readonly Color ActivityColor = Color.FromArgb(255, 198, 86);
    private static readonly Color WarningColor = Color.FromArgb(255, 160, 90);
    private static readonly Color CriticalColor = Color.FromArgb(255, 72, 72);
    private static readonly Color ButtonColor = Color.FromArgb(42, 71, 94);

    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RimTypeHid = 2;
    private const uint RidevDeviceNotify = 0x00002000;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort JoystickUsage = 0x04;
    private const ushort GamepadUsage = 0x05;
    private const ushort SonyVendorId = 0x054C;

    private readonly string gameDirectory;
    private readonly string configPath;
    private readonly ComboBox playerOneDevice = new ComboBox();
    private readonly ComboBox playerTwoDevice = new ComboBox();
    private readonly CheckBox debugOverlay = new CheckBox();
    private readonly Label[] slotStatus = new Label[4];
    private readonly Label hidStatus = new Label();
    private readonly Label lastInputStatus = new Label();
    private readonly Label notice = new Label();
    private readonly Button useLastForHugh = new Button();
    private readonly Button useLastForDiana = new Button();
    private readonly Timer pollTimer = new Timer();
    private readonly XInputSnapshot[] xinputSnapshots = new XInputSnapshot[4];
    private readonly Dictionary<IntPtr, SonyHidController> sonyControllers = new Dictionary<IntPtr, SonyHidController>();

    private int refreshTick;
    private int lastActiveXInputSlot = -1;
    private IntPtr lastActiveHidDevice = IntPtr.Zero;
    private DateTime lastInputTime = DateTime.MinValue;
    private bool rawInputRegistered;

    public ConfiguratorForm()
    {
        gameDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        configPath = Path.Combine(gameDirectory, "reframework", "data", "PragmataSplitControl.ini");

        Text = "PRAGMATA Split Control - Controller Setup";
        ClientSize = new Size(860, 708);
        MinimumSize = new Size(876, 747);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = WindowColor;
        ForeColor = TextColor;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        DoubleBuffered = true;

        for (int slot = 0; slot < xinputSnapshots.Length; slot++)
            xinputSnapshots[slot] = new XInputSnapshot();

        BuildInterface();
        LoadConfiguration();

        pollTimer.Interval = 33;
        pollTimer.Tick += OnPollTimer;
        pollTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterForRawControllerInput();
        RefreshSonyControllers();
        PollXInputControllers();
        UpdateControllerDisplay();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        pollTimer.Stop();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
            HandleRawInput(message.LParam);
        else if (message.Msg == WmInputDeviceChange)
            RefreshSonyControllers();

        base.WndProc(ref message);
    }

    private void BuildInterface()
    {
        Controls.Add(MakeLabel("PRAGMATA SPLIT CONTROL", 24, 18, 810, 31, 16f, AccentColor));
        Controls.Add(MakeLabel("Controller assignment and live input test", 26, 51, 810, 23, 9.5f, MutedColor));

        Panel assignments = MakePanel(24, 86, 812, 205);
        Controls.Add(assignments);
        assignments.Controls.Add(MakeLabel("CONTROLLER ASSIGNMENTS", 18, 13, 350, 24, 10.5f, TextColor));

        assignments.Controls.Add(MakeLabel("Hugh input", 18, 46, 150, 24, 10f, TextColor));
        PrepareCombo(playerOneDevice, 174, 43, 610);
        playerOneDevice.Items.Add(new PlayerOneChoice("Keyboard + mouse (all XInput gamepads hidden)", "KeyboardMouse", -1));
        playerOneDevice.Items.Add(new PlayerOneChoice("Native PlayStation HID + keyboard/mouse (all XInput hidden)", "NativeDualSense", -1));
        for (int slot = 0; slot < 4; slot++)
            playerOneDevice.Items.Add(new PlayerOneChoice("XInput slot " + slot + " + keyboard/mouse", "XInput", slot));
        playerOneDevice.SelectedIndexChanged += OnAssignmentChanged;
        assignments.Controls.Add(playerOneDevice);

        assignments.Controls.Add(MakeLabel("Keyboard and mouse are never filtered. This choice only controls Hugh's gamepad path.", 174, 72, 610, 21, 8.5f, MutedColor));

        assignments.Controls.Add(MakeLabel("Player 2 - Diana", 18, 105, 150, 24, 10f, TextColor));
        PrepareCombo(playerTwoDevice, 174, 102, 610);
        for (int slot = 0; slot < 4; slot++)
            playerTwoDevice.Items.Add(new PlayerTwoChoice("XInput slot " + slot, slot));
        playerTwoDevice.SelectedIndexChanged += OnAssignmentChanged;
        assignments.Controls.Add(playerTwoDevice);

        assignments.Controls.Add(MakeLabel("Diana uses XInput. With Steam Input enabled, controller slot numbers may differ or change.", 174, 131, 610, 21, 8.5f, MutedColor));

        debugOverlay.Text = "Enable in-game diagnostic overlay";
        debugOverlay.Location = new Point(18, 166);
        debugOverlay.Size = new Size(310, 23);
        debugOverlay.ForeColor = TextColor;
        debugOverlay.BackColor = Color.Transparent;
        assignments.Controls.Add(debugOverlay);

        Label nativeDualSenseWarning = MakeLabel("NATIVE DUALSENSE: DISABLE STEAM INPUT", 361, 166, 423, 23, 8.5f, CriticalColor);
        nativeDualSenseWarning.Font = new Font("Segoe UI", 11.4f, FontStyle.Bold, GraphicsUnit.Pixel);
        assignments.Controls.Add(nativeDualSenseWarning);

        Panel tester = MakePanel(24, 304, 812, 300);
        Controls.Add(tester);
        tester.Controls.Add(MakeLabel("LIVE CONTROLLER TEST", 18, 12, 280, 24, 10.5f, TextColor));
        tester.Controls.Add(MakeLabel("Press a button or move a stick to identify the device and its XInput slot.", 294, 13, 490, 23, 8.5f, MutedColor));

        lastInputStatus.Location = new Point(18, 39);
        lastInputStatus.Size = new Size(766, 22);
        lastInputStatus.ForeColor = ActivityColor;
        lastInputStatus.BackColor = Color.Transparent;
        lastInputStatus.Text = "Last input: waiting for a controller...";
        tester.Controls.Add(lastInputStatus);

        for (int slot = 0; slot < 4; slot++)
        {
            slotStatus[slot] = MakeLabel("XInput slot " + slot + ": checking...", 18, 65 + slot * 34, 766, 30, 9f, MutedColor);
            slotStatus[slot].BorderStyle = BorderStyle.FixedSingle;
            slotStatus[slot].Padding = new Padding(7, 5, 4, 3);
            tester.Controls.Add(slotStatus[slot]);
        }

        tester.Controls.Add(MakeLabel("NATIVE PLAYSTATION HID", 18, 205, 250, 22, 9.5f, TextColor));
        tester.Controls.Add(MakeLabel("The test reads DualSense and DUALSHOCK 4 directly over USB or Bluetooth.", 275, 205, 509, 22, 8.5f, MutedColor));

        hidStatus.Location = new Point(18, 229);
        hidStatus.Size = new Size(766, 57);
        hidStatus.ForeColor = MutedColor;
        hidStatus.BackColor = WindowColor;
        hidStatus.BorderStyle = BorderStyle.FixedSingle;
        hidStatus.Padding = new Padding(7, 5, 4, 3);
        hidStatus.Text = "Scanning for PlayStation controllers...";
        tester.Controls.Add(hidStatus);

        Button refresh = MakeButton("Refresh devices", 24, 620, 150);
        refresh.Click += delegate { RefreshAllControllers(true); };
        Controls.Add(refresh);

        PrepareActionButton(useLastForHugh, "Use last input for Hugh", 188, 620, 190);
        useLastForHugh.Click += UseLastInputForHugh;
        Controls.Add(useLastForHugh);

        PrepareActionButton(useLastForDiana, "Use last XInput for Diana", 392, 620, 210);
        useLastForDiana.Click += UseLastInputForDiana;
        Controls.Add(useLastForDiana);

        Button save = MakeButton("Save configuration", 652, 620, 184);
        save.BackColor = Color.FromArgb(44, 105, 139);
        save.Click += delegate { SaveConfiguration(); };
        Controls.Add(save);

        notice.Location = new Point(26, 665);
        notice.Size = new Size(808, 31);
        notice.ForeColor = MutedColor;
        notice.TextAlign = ContentAlignment.MiddleLeft;
        notice.Text = "Assignments are applied the next time PRAGMATA starts.";
        Controls.Add(notice);

        useLastForHugh.Enabled = false;
        useLastForDiana.Enabled = false;
    }

    private static Panel MakePanel(int x, int y, int width, int height)
    {
        Panel panel = new Panel();
        panel.Location = new Point(x, y);
        panel.Size = new Size(width, height);
        panel.BackColor = PanelColor;
        panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        return panel;
    }

    private static Label MakeLabel(string text, int x, int y, int width, int height, float size, Color color)
    {
        Label label = new Label();
        label.Text = text;
        label.Location = new Point(x, y);
        label.Size = new Size(width, height);
        label.ForeColor = color;
        label.BackColor = Color.Transparent;
        label.Font = new Font("Segoe UI", size * 1.34f, FontStyle.Regular, GraphicsUnit.Pixel);
        return label;
    }

    private static void PrepareCombo(ComboBox combo, int x, int y, int width)
    {
        combo.Location = new Point(x, y);
        combo.Size = new Size(width, 28);
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Color.FromArgb(43, 53, 63);
        combo.ForeColor = TextColor;
    }

    private static Button MakeButton(string text, int x, int y, int width)
    {
        Button button = new Button();
        PrepareActionButton(button, text, x, y, width);
        return button;
    }

    private static void PrepareActionButton(Button button, string text, int x, int y, int width)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(width, 35);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(77, 112, 135);
        button.BackColor = ButtonColor;
        button.ForeColor = TextColor;
        button.Cursor = Cursors.Hand;
    }

    private void LoadConfiguration()
    {
        string mode = "XInput";
        int p1Slot = 0;
        int p2Slot = 1;
        bool debug = false;

        try
        {
            if (File.Exists(configPath))
            {
                string[] lines = File.ReadAllLines(configPath);
                foreach (string sourceLine in lines)
                {
                    string line = sourceLine.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                        continue;

                    int separator = line.IndexOf('=');
                    if (separator < 1)
                        continue;

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    int parsed;
                    if (key.Equals("Player1Mode", StringComparison.OrdinalIgnoreCase))
                        mode = value;
                    else if (key.Equals("Player1XInputSlot", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsed))
                        p1Slot = ClampSlot(parsed);
                    else if (key.Equals("Player2XInputSlot", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsed))
                        p2Slot = ClampSlot(parsed);
                    else if (key.Equals("DebugOverlay", StringComparison.OrdinalIgnoreCase))
                        bool.TryParse(value, out debug);
                }
            }

            if (mode.Equals("KeyboardMouse", StringComparison.OrdinalIgnoreCase))
                playerOneDevice.SelectedIndex = 0;
            else if (mode.Equals("NativeDualSense", StringComparison.OrdinalIgnoreCase) || mode.Equals("NativePlayStation", StringComparison.OrdinalIgnoreCase))
                playerOneDevice.SelectedIndex = 1;
            else
                playerOneDevice.SelectedIndex = 2 + p1Slot;

            playerTwoDevice.SelectedIndex = p2Slot;
            debugOverlay.Checked = debug;
            notice.Text = File.Exists(configPath)
                ? "Current configuration loaded. Press buttons below to verify the assignments."
                : "No configuration found. Choose controllers and save before starting PRAGMATA.";
        }
        catch (Exception exception)
        {
            playerOneDevice.SelectedIndex = 2;
            playerTwoDevice.SelectedIndex = 1;
            debugOverlay.Checked = false;
            notice.ForeColor = WarningColor;
            notice.Text = "Could not read the configuration: " + exception.Message;
        }

        UpdateAssignmentMarkers();
    }

    private void SaveConfiguration()
    {
        PlayerOneChoice p1 = playerOneDevice.SelectedItem as PlayerOneChoice;
        PlayerTwoChoice p2 = playerTwoDevice.SelectedItem as PlayerTwoChoice;
        if (p1 == null || p2 == null)
            return;

        if (p1.Mode == "XInput" && p1.Slot == p2.Slot)
        {
            MessageBox.Show(this,
                "Hugh and Diana cannot use the same XInput slot.\r\n\r\nPress a button in the live test to identify each controller, then select different slots.",
                "Controller assignment conflict",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            string directory = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            int p1Slot = p1.Slot < 0 ? 0 : p1.Slot;
            string contents =
                "[Input]\r\n" +
                "; Generated by PRAGMATA Split Control\r\n" +
                "Player1Mode=" + p1.Mode + "\r\n" +
                "Player1XInputSlot=" + p1Slot + "\r\n" +
                "Player2XInputSlot=" + p2.Slot + "\r\n" +
                "\r\n[Debug]\r\n" +
                "DebugOverlay=" + (debugOverlay.Checked ? "true" : "false") + "\r\n";
            File.WriteAllText(configPath, contents, new UTF8Encoding(false));

            bool gameRunning = Process.GetProcessesByName("PRAGMATA").Length != 0;
            bool p2Connected = xinputSnapshots[p2.Slot].Connected;
            notice.ForeColor = p2Connected ? AccentColor : WarningColor;
            if (gameRunning)
                notice.Text = "Saved. PRAGMATA is running; restart the game to apply these assignments.";
            else if (!p2Connected)
                notice.Text = "Saved. Diana's XInput slot is not connected right now; connect it before starting the game.";
            else
                notice.Text = "Saved. The controller assignments are ready for the next PRAGMATA launch.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                "Could not save the configuration:\r\n" + exception.Message,
                "PRAGMATA Split Control",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnAssignmentChanged(object sender, EventArgs e)
    {
        UpdateAssignmentMarkers();
    }

    private void UpdateAssignmentMarkers()
    {
        if (slotStatus[0] == null)
            return;

        UpdateControllerDisplay();
    }

    private void OnPollTimer(object sender, EventArgs e)
    {
        PollXInputControllers();
        refreshTick++;
        if (refreshTick >= 60)
        {
            refreshTick = 0;
            RefreshSonyControllers();
        }

        UpdateControllerDisplay();
    }

    private void RefreshAllControllers(bool userRequested)
    {
        RefreshSonyControllers();
        PollXInputControllers();
        UpdateControllerDisplay();
        if (userRequested)
        {
            notice.ForeColor = MutedColor;
            notice.Text = "Device list refreshed. Press a button on each controller to identify it.";
        }
    }

    private void PollXInputControllers()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            XInputState state;
            bool connected = TryGetXInputState(slot, out state);
            XInputSnapshot snapshot = xinputSnapshots[slot];

            if (connected)
            {
                bool changed = snapshot.Connected && HasMeaningfulXInputChange(snapshot.State.Gamepad, state.Gamepad);
                bool firstMeaningfulState = !snapshot.Connected && IsMeaningfulState(state.Gamepad);
                snapshot.Connected = true;
                snapshot.State = state;
                if (changed || firstMeaningfulState)
                {
                    snapshot.LastActivity = DateTime.UtcNow;
                    lastActiveXInputSlot = slot;
                    lastActiveHidDevice = IntPtr.Zero;
                    lastInputTime = DateTime.UtcNow;
                }
            }
            else
            {
                snapshot.Connected = false;
                snapshot.State = new XInputState();
                snapshot.LastActivity = DateTime.MinValue;
            }
        }
    }

    private void UpdateControllerDisplay()
    {
        PlayerOneChoice p1 = playerOneDevice.SelectedItem as PlayerOneChoice;
        PlayerTwoChoice p2 = playerTwoDevice.SelectedItem as PlayerTwoChoice;
        DateTime now = DateTime.UtcNow;

        for (int slot = 0; slot < 4; slot++)
        {
            XInputSnapshot snapshot = xinputSnapshots[slot];
            bool assignedP1 = p1 != null && p1.Mode == "XInput" && p1.Slot == slot;
            bool assignedP2 = p2 != null && p2.Slot == slot;
            bool recentlyActive = snapshot.LastActivity != DateTime.MinValue && (now - snapshot.LastActivity).TotalMilliseconds < 500.0;

            string assignment = assignedP1 && assignedP2 ? " [HUGH + DIANA: CONFLICT]" : assignedP1 ? " [HUGH]" : assignedP2 ? " [DIANA]" : string.Empty;
            if (!snapshot.Connected)
            {
                slotStatus[slot].Text = "XInput slot " + slot + assignment + "  -  not connected";
                slotStatus[slot].ForeColor = assignedP1 || assignedP2 ? WarningColor : MutedColor;
            }
            else
            {
                slotStatus[slot].Text = "XInput slot " + slot + assignment + "  -  " + FormatXInputState(snapshot.State.Gamepad);
                if (assignedP1 && assignedP2)
                    slotStatus[slot].ForeColor = WarningColor;
                else if (recentlyActive)
                    slotStatus[slot].ForeColor = ActivityColor;
                else if (assignedP2)
                    slotStatus[slot].ForeColor = PlayerTwoColor;
                else if (assignedP1)
                    slotStatus[slot].ForeColor = AccentColor;
                else
                    slotStatus[slot].ForeColor = TextColor;
            }
        }

        UpdateSonyControllerDisplay(now, p1);
        UpdateLastInputDisplay();
    }

    private void UpdateSonyControllerDisplay(DateTime now, PlayerOneChoice p1)
    {
        if (!rawInputRegistered)
        {
            hidStatus.ForeColor = WarningColor;
            hidStatus.Text = "Raw HID input registration failed. XInput testing still works.";
            return;
        }

        if (sonyControllers.Count == 0)
        {
            hidStatus.ForeColor = p1 != null && p1.Mode == "NativeDualSense" ? WarningColor : MutedColor;
            hidStatus.Text = "No native DualSense or DUALSHOCK 4 detected. If the pad only appears above, it is currently exposed as XInput.";
            return;
        }

        List<string> lines = new List<string>();
        bool recent = false;
        foreach (SonyHidController controller in sonyControllers.Values)
        {
            if (controller.LastActivity != DateTime.MinValue && (now - controller.LastActivity).TotalMilliseconds < 500.0)
                recent = true;

            string state = controller.State.Valid ? FormatSonyState(controller.State) : "waiting for input";
            lines.Add(controller.DisplayName + " [VID 054C / PID " + controller.ProductId.ToString("X4") + ", " + controller.Connection + "]  -  " + state);
            if (lines.Count == 2)
                break;
        }

        hidStatus.ForeColor = recent ? ActivityColor : (p1 != null && p1.Mode == "NativeDualSense" ? AccentColor : TextColor);
        hidStatus.Text = string.Join(Environment.NewLine, lines.ToArray());
    }

    private void UpdateLastInputDisplay()
    {
        if (lastInputTime == DateTime.MinValue)
        {
            lastInputStatus.Text = "Last input: waiting for a controller...";
            useLastForHugh.Enabled = false;
            useLastForDiana.Enabled = false;
            return;
        }

        if (lastActiveXInputSlot >= 0)
        {
            lastInputStatus.Text = "Last input: XInput slot " + lastActiveXInputSlot + " (can be assigned to Hugh or Diana)";
            useLastForHugh.Enabled = true;
            useLastForDiana.Enabled = true;
            return;
        }

        SonyHidController controller;
        if (lastActiveHidDevice != IntPtr.Zero && sonyControllers.TryGetValue(lastActiveHidDevice, out controller))
        {
            lastInputStatus.Text = "Last input: " + controller.DisplayName + " over native HID (direct assignment is available for Hugh)";
            useLastForHugh.Enabled = true;
            useLastForDiana.Enabled = true;
            return;
        }

        lastInputStatus.Text = "Last input: controller disconnected";
        useLastForHugh.Enabled = false;
        useLastForDiana.Enabled = false;
    }

    private void UseLastInputForHugh(object sender, EventArgs e)
    {
        if (lastActiveXInputSlot >= 0)
        {
            playerOneDevice.SelectedIndex = 2 + lastActiveXInputSlot;
            notice.ForeColor = AccentColor;
            notice.Text = "Hugh assigned to XInput slot " + lastActiveXInputSlot + ". Save the configuration when ready.";
        }
        else if (lastActiveHidDevice != IntPtr.Zero)
        {
            playerOneDevice.SelectedIndex = 1;
            notice.ForeColor = AccentColor;
            notice.Text = "Hugh assigned to the game's native PlayStation HID controller path.";
        }
    }

    private void UseLastInputForDiana(object sender, EventArgs e)
    {
        if (lastActiveXInputSlot >= 0)
        {
            playerTwoDevice.SelectedIndex = lastActiveXInputSlot;
            notice.ForeColor = PlayerTwoColor;
            notice.Text = "Diana assigned to XInput slot " + lastActiveXInputSlot + ". Save the configuration when ready.";
            return;
        }

        if (lastActiveHidDevice != IntPtr.Zero)
        {
            MessageBox.Show(this,
                "Diana's input backend reads XInput slots. To use this PlayStation controller for Diana, enable Steam Input so it is exposed as XInput. Steam Input may assign a different slot, so press a button again and select the slot shown in the live test.\r\n\r\nFor native DualSense input and adaptive triggers on Hugh, disable Steam Input and assign Native PlayStation HID to Hugh.",
                "Native PlayStation controller detected",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void RegisterForRawControllerInput()
    {
        RawInputDevice[] devices = new RawInputDevice[2];
        devices[0].UsagePage = GenericDesktopPage;
        devices[0].Usage = JoystickUsage;
        devices[0].Flags = RidevDeviceNotify;
        devices[0].Target = Handle;
        devices[1].UsagePage = GenericDesktopPage;
        devices[1].Usage = GamepadUsage;
        devices[1].Flags = RidevDeviceNotify;
        devices[1].Target = Handle;

        rawInputRegistered = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RawInputDevice)));
    }

    private void RefreshSonyControllers()
    {
        Dictionary<IntPtr, SonyHidController> refreshed = new Dictionary<IntPtr, SonyHidController>();
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf(typeof(RawInputDeviceList));
        if (GetRawInputDeviceList(null, ref count, itemSize) == uint.MaxValue || count == 0)
        {
            sonyControllers.Clear();
            return;
        }

        RawInputDeviceList[] devices = new RawInputDeviceList[count];
        if (GetRawInputDeviceList(devices, ref count, itemSize) == uint.MaxValue)
            return;

        for (int index = 0; index < count; index++)
        {
            if (devices[index].Type != RimTypeHid)
                continue;

            RawDeviceInfo info = new RawDeviceInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(RawDeviceInfo));
            uint infoSize = info.Size;
            if (GetRawInputDeviceInfo(devices[index].Device, RidiDeviceInfo, ref info, ref infoSize) == uint.MaxValue)
                continue;
            if (info.Type != RimTypeHid || info.Hid.VendorId != SonyVendorId)
                continue;

            string path = GetRawDevicePath(devices[index].Device);
            SonyHidController existing;
            if (sonyControllers.TryGetValue(devices[index].Device, out existing))
            {
                existing.Path = path;
                existing.Connection = DetectConnection(path);
                refreshed[devices[index].Device] = existing;
            }
            else
            {
                SonyHidController controller = new SonyHidController();
                controller.Device = devices[index].Device;
                controller.ProductId = (ushort)info.Hid.ProductId;
                controller.DisplayName = GetSonyProductName(controller.ProductId);
                controller.Path = path;
                controller.Connection = DetectConnection(path);
                refreshed[controller.Device] = controller;
            }
        }

        sonyControllers.Clear();
        foreach (KeyValuePair<IntPtr, SonyHidController> pair in refreshed)
            sonyControllers[pair.Key] = pair.Value;

        if (lastActiveHidDevice != IntPtr.Zero && !sonyControllers.ContainsKey(lastActiveHidDevice))
            lastActiveHidDevice = IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0)
            return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
                return;

            RawInputHeader header = (RawInputHeader)Marshal.PtrToStructure(buffer, typeof(RawInputHeader));
            if (header.Type != RimTypeHid)
                return;

            SonyHidController controller;
            if (!sonyControllers.TryGetValue(header.Device, out controller))
            {
                RefreshSonyControllers();
                if (!sonyControllers.TryGetValue(header.Device, out controller))
                    return;
            }

            int hidOffset = Marshal.SizeOf(typeof(RawInputHeader));
            uint reportSize = (uint)Marshal.ReadInt32(buffer, hidOffset);
            uint reportCount = (uint)Marshal.ReadInt32(buffer, hidOffset + 4);
            if (reportSize == 0 || reportCount == 0 || reportSize > 1024)
                return;

            byte[] report = new byte[reportSize];
            Marshal.Copy(IntPtr.Add(buffer, hidOffset + 8), report, 0, (int)reportSize);

            SonyPadState decoded = DecodeSonyState(controller.ProductId, report);
            if (!decoded.Valid)
                return;

            bool meaningfulChange = IsMeaningfulSonyChange(controller.State, decoded);
            controller.State = decoded;
            if (meaningfulChange)
            {
                controller.LastActivity = DateTime.UtcNow;
                lastActiveHidDevice = controller.Device;
                lastActiveXInputSlot = -1;
                lastInputTime = DateTime.UtcNow;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SonyPadState DecodeSonyState(ushort productId, byte[] report)
    {
        SonyPadState state = new SonyPadState();
        if (report == null || report.Length < 10)
            return state;

        bool dualShock4 = productId == 0x05C4 || productId == 0x09CC || productId == 0x0BA0;
        bool dualSense = productId == 0x0CE6 || productId == 0x0DF2;
        int dataOffset;
        int buttonOffset;
        int triggerOffset;

        if (dualShock4)
        {
            dataOffset = report[0] == 0x11 ? 3 : (report[0] == 0x01 ? 1 : 0);
            buttonOffset = dataOffset + 4;
            triggerOffset = dataOffset + 7;
        }
        else if (dualSense)
        {
            dataOffset = report[0] == 0x31 ? 2 : (report[0] == 0x01 ? 1 : 0);
            buttonOffset = dataOffset + 7;
            triggerOffset = dataOffset + 4;
        }
        else
        {
            dataOffset = report[0] == 0x01 ? 1 : 0;
            buttonOffset = dataOffset + 4;
            triggerOffset = dataOffset + 7;
        }

        if (dataOffset + 3 >= report.Length || buttonOffset + 2 >= report.Length || triggerOffset + 1 >= report.Length)
            return state;

        state.Valid = true;
        state.LeftX = report[dataOffset];
        state.LeftY = report[dataOffset + 1];
        state.RightX = report[dataOffset + 2];
        state.RightY = report[dataOffset + 3];
        state.Buttons0 = report[buttonOffset];
        state.Buttons1 = report[buttonOffset + 1];
        state.Buttons2 = report[buttonOffset + 2];
        state.LeftTrigger = report[triggerOffset];
        state.RightTrigger = report[triggerOffset + 1];
        return state;
    }

    private static bool IsMeaningfulSonyChange(SonyPadState previous, SonyPadState current)
    {
        if (!current.Valid)
            return false;

        if (!previous.Valid)
        {
            return (current.Buttons0 & 0xF0) != 0 || current.Buttons1 != 0 || current.Buttons2 != 0 ||
                   current.LeftTrigger > 24 || current.RightTrigger > 24 ||
                   Math.Abs(current.LeftX - 128) > 24 || Math.Abs(current.LeftY - 128) > 24 ||
                   Math.Abs(current.RightX - 128) > 24 || Math.Abs(current.RightY - 128) > 24;
        }

        return previous.Buttons0 != current.Buttons0 || previous.Buttons1 != current.Buttons1 || previous.Buttons2 != current.Buttons2 ||
               Math.Abs(previous.LeftTrigger - current.LeftTrigger) > 8 || Math.Abs(previous.RightTrigger - current.RightTrigger) > 8 ||
               Math.Abs(previous.LeftX - current.LeftX) > 8 || Math.Abs(previous.LeftY - current.LeftY) > 8 ||
               Math.Abs(previous.RightX - current.RightX) > 8 || Math.Abs(previous.RightY - current.RightY) > 8;
    }

    private static string FormatSonyState(SonyPadState state)
    {
        List<string> buttons = new List<string>();
        byte face = state.Buttons0;
        int dpad = face & 0x0F;
        if ((face & 0x10) != 0) buttons.Add("Square");
        if ((face & 0x20) != 0) buttons.Add("Cross");
        if ((face & 0x40) != 0) buttons.Add("Circle");
        if ((face & 0x80) != 0) buttons.Add("Triangle");
        AddDpadButtons(buttons, dpad);
        if ((state.Buttons1 & 0x01) != 0) buttons.Add("L1");
        if ((state.Buttons1 & 0x02) != 0) buttons.Add("R1");
        if ((state.Buttons1 & 0x04) != 0) buttons.Add("L2");
        if ((state.Buttons1 & 0x08) != 0) buttons.Add("R2");
        if ((state.Buttons1 & 0x10) != 0) buttons.Add("Create/Share");
        if ((state.Buttons1 & 0x20) != 0) buttons.Add("Options");
        if ((state.Buttons1 & 0x40) != 0) buttons.Add("L3");
        if ((state.Buttons1 & 0x80) != 0) buttons.Add("R3");
        if ((state.Buttons2 & 0x01) != 0) buttons.Add("PS");
        if ((state.Buttons2 & 0x02) != 0) buttons.Add("Touchpad");

        string pressed = buttons.Count == 0 ? "neutral" : string.Join("+", buttons.ToArray());
        return pressed + " | L2 " + state.LeftTrigger + " R2 " + state.RightTrigger +
               " | LS " + FormatByteAxis(state.LeftX) + "," + FormatByteAxisInverted(state.LeftY) +
               " RS " + FormatByteAxis(state.RightX) + "," + FormatByteAxisInverted(state.RightY);
    }

    private static void AddDpadButtons(List<string> buttons, int dpad)
    {
        if (dpad == 0 || dpad == 1 || dpad == 7) buttons.Add("DUp");
        if (dpad == 1 || dpad == 2 || dpad == 3) buttons.Add("DRight");
        if (dpad == 3 || dpad == 4 || dpad == 5) buttons.Add("DDown");
        if (dpad == 5 || dpad == 6 || dpad == 7) buttons.Add("DLeft");
    }

    private static string FormatXInputState(XInputGamepad pad)
    {
        List<string> buttons = new List<string>();
        AddXInputButton(buttons, pad.Buttons, 0x0001, "DUp");
        AddXInputButton(buttons, pad.Buttons, 0x0002, "DDown");
        AddXInputButton(buttons, pad.Buttons, 0x0004, "DLeft");
        AddXInputButton(buttons, pad.Buttons, 0x0008, "DRight");
        AddXInputButton(buttons, pad.Buttons, 0x0010, "Start");
        AddXInputButton(buttons, pad.Buttons, 0x0020, "Back");
        AddXInputButton(buttons, pad.Buttons, 0x0040, "L3");
        AddXInputButton(buttons, pad.Buttons, 0x0080, "R3");
        AddXInputButton(buttons, pad.Buttons, 0x0100, "LB");
        AddXInputButton(buttons, pad.Buttons, 0x0200, "RB");
        AddXInputButton(buttons, pad.Buttons, 0x1000, "A");
        AddXInputButton(buttons, pad.Buttons, 0x2000, "B");
        AddXInputButton(buttons, pad.Buttons, 0x4000, "X");
        AddXInputButton(buttons, pad.Buttons, 0x8000, "Y");

        string pressed = buttons.Count == 0 ? "neutral" : string.Join("+", buttons.ToArray());
        return pressed + " | LT " + pad.LeftTrigger + " RT " + pad.RightTrigger +
               " | LS " + FormatShortAxis(pad.ThumbLX) + "," + FormatShortAxis(pad.ThumbLY) +
               " RS " + FormatShortAxis(pad.ThumbRX) + "," + FormatShortAxis(pad.ThumbRY);
    }

    private static void AddXInputButton(List<string> buttons, ushort current, ushort mask, string name)
    {
        if ((current & mask) != 0)
            buttons.Add(name);
    }

    private static bool HasMeaningfulXInputChange(XInputGamepad previous, XInputGamepad current)
    {
        return previous.Buttons != current.Buttons ||
               Math.Abs(previous.LeftTrigger - current.LeftTrigger) > 4 ||
               Math.Abs(previous.RightTrigger - current.RightTrigger) > 4 ||
               Math.Abs(previous.ThumbLX - current.ThumbLX) > 1500 ||
               Math.Abs(previous.ThumbLY - current.ThumbLY) > 1500 ||
               Math.Abs(previous.ThumbRX - current.ThumbRX) > 1500 ||
               Math.Abs(previous.ThumbRY - current.ThumbRY) > 1500;
    }

    private static bool IsMeaningfulState(XInputGamepad pad)
    {
        return pad.Buttons != 0 || pad.LeftTrigger > 24 || pad.RightTrigger > 24 ||
               Math.Abs((int)pad.ThumbLX) > 6000 || Math.Abs((int)pad.ThumbLY) > 6000 ||
               Math.Abs((int)pad.ThumbRX) > 6000 || Math.Abs((int)pad.ThumbRY) > 6000;
    }

    private static string FormatShortAxis(short value)
    {
        double normalized = value >= 0 ? value / 32767.0 : value / 32768.0;
        if (Math.Abs(normalized) < 0.02)
            normalized = 0.0;
        return normalized.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatByteAxis(byte value)
    {
        double normalized = (value - 127.5) / 127.5;
        if (Math.Abs(normalized) < 0.03)
            normalized = 0.0;
        return normalized.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatByteAxisInverted(byte value)
    {
        double normalized = (127.5 - value) / 127.5;
        if (Math.Abs(normalized) < 0.03)
            normalized = 0.0;
        return normalized.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    }

    private static string GetSonyProductName(ushort productId)
    {
        switch (productId)
        {
            case 0x0268: return "DUALSHOCK 3";
            case 0x05C4: return "DUALSHOCK 4 (CUH-ZCT1)";
            case 0x09CC: return "DUALSHOCK 4 (CUH-ZCT2)";
            case 0x0BA0: return "DUALSHOCK 4 USB Wireless Adaptor";
            case 0x0CE6: return "DualSense";
            case 0x0DF2: return "DualSense Edge";
            default: return "Sony PlayStation controller";
        }
    }

    private static string DetectConnection(string path)
    {
        if (!string.IsNullOrEmpty(path) &&
            (path.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0))
            return "Bluetooth";
        return "USB/HID";
    }

    private static string GetRawDevicePath(IntPtr device)
    {
        uint characterCount = 0;
        GetRawInputDeviceInfo(device, RidiDeviceName, IntPtr.Zero, ref characterCount);
        if (characterCount == 0)
            return string.Empty;

        StringBuilder builder = new StringBuilder((int)characterCount + 1);
        if (GetRawInputDeviceInfo(device, RidiDeviceName, builder, ref characterCount) == uint.MaxValue)
            return string.Empty;
        return builder.ToString();
    }

    private static bool TryGetXInputState(int slot, out XInputState state)
    {
        try
        {
            return XInputGetState((uint)slot, out state) == 0;
        }
        catch
        {
            state = new XInputState();
            return false;
        }
    }

    private static int ClampSlot(int value)
    {
        return value < 0 ? 0 : (value > 3 ? 3 : value);
    }

    [DllImport("XINPUT9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint deviceCount, uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RawInputDeviceList[] devices, ref uint deviceCount, uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RawDeviceInfo data, ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public IntPtr Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDeviceInfo
    {
        public uint Size;
        public uint Type;
        public RawDeviceInfoHid Hid;
        public ulong Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDeviceInfoHid
    {
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    private struct SonyPadState
    {
        public bool Valid;
        public byte LeftX;
        public byte LeftY;
        public byte RightX;
        public byte RightY;
        public byte Buttons0;
        public byte Buttons1;
        public byte Buttons2;
        public byte LeftTrigger;
        public byte RightTrigger;
    }

    private sealed class XInputSnapshot
    {
        public bool Connected;
        public XInputState State;
        public DateTime LastActivity = DateTime.MinValue;
    }

    private sealed class SonyHidController
    {
        public IntPtr Device;
        public ushort ProductId;
        public string DisplayName = string.Empty;
        public string Path = string.Empty;
        public string Connection = string.Empty;
        public SonyPadState State;
        public DateTime LastActivity = DateTime.MinValue;
    }

    private sealed class PlayerOneChoice
    {
        public readonly string Label;
        public readonly string Mode;
        public readonly int Slot;

        public PlayerOneChoice(string label, string mode, int slot)
        {
            Label = label;
            Mode = mode;
            Slot = slot;
        }

        public override string ToString()
        {
            return Label;
        }
    }

    private sealed class PlayerTwoChoice
    {
        public readonly string Label;
        public readonly int Slot;

        public PlayerTwoChoice(string label, int slot)
        {
            Label = label;
            Slot = slot;
        }

        public override string ToString()
        {
            return Label;
        }
    }
}
