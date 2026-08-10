using System;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Controls;
using Blish_HUD.GameServices.ArcDps.V2;
using Blish_HUD.GameServices.ArcDps.V2.Models.UnofficialExtras;
using Blish_HUD.Modules;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;

namespace Gw2CommanderTranslator {
    /// <summary>
    /// A deliberately small diagnostic module.  It proves that the ArcDPS bridge can
    /// deliver Squad Chat to a Blish HUD module before the tactical translator is built.
    /// </summary>
    [Export(typeof(Module))]
    public sealed class CommanderTranslatorModule : Module {
        private const string DiagnosticLogDirectoryName = "Logs";
        private const int MaximumDisplayLength = 220;

        private static readonly Logger Logger = Logger.GetLogger<CommanderTranslatorModule>();

        private readonly ConcurrentQueue<CapturedSquadMessage> _pendingMessages = new ConcurrentQueue<CapturedSquadMessage>();
        private readonly ConcurrentQueue<string> _pendingDiagnosticLogLines = new ConcurrentQueue<string>();
        private readonly Stopwatch _uptime = Stopwatch.StartNew();

        private ArcDpsMessageListener<SquadMessageInfo> _squadMessageListener;
        private FlowPanel _diagnosticPanel;
        private Label _connectionLabel;
        private Label _counterLabel;
        private Label _lastMessageLabel;
        private SettingEntry<bool> _rawMessageLogging;
        private StreamWriter _diagnosticLogWriter;
        private string _diagnosticLogPath;
        private bool _diagnosticLogUnavailable;
        private volatile bool _acceptMessages;
        private long _receivedCount;
        private long _lastMessageReceivedTicks = -1;
        private string _lastMessageSummary = "No Squad Chat captured yet.";

        [ImportingConstructor]
        public CommanderTranslatorModule([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters) {
        }

        protected override void DefineSettings(SettingCollection settings) {
            _rawMessageLogging = settings.DefineSetting(
                "raw squad chat diagnostic log",
                false,
                () => "Record raw Squad Chat to an independent log file",
                () => "Off by default. Enable only while testing; chat text, account names, and character names are written to a daily log file owned by this module.");
        }

        protected override Task LoadAsync() {
            CreateDiagnosticPanel();

            _acceptMessages = true;
            _squadMessageListener = new ArcDpsMessageListener<SquadMessageInfo>(
                MessageType.SquadMessage,
                HandleSquadMessageAsync);

            // Registration is safe before the bridge connects: ArcDpsV2 keeps the listener
            // and registers it when a compatible ArcDPS bridge becomes available.
            GameService.ArcDpsV2.RegisterMessageType(_squadMessageListener);

            Logger.Info("Commander Translator capture spike loaded. Waiting for ArcDPS bridge SquadMessage support.");
            return Task.CompletedTask;
        }

        protected override void Update(GameTime gameTime) {
            SynchronizeDiagnosticLog();
            DrainCapturedMessages();
            RefreshDiagnosticPanel();
        }

        protected override void Unload() {
            // ArcDpsV2 can keep listener registrations for a future bridge reconnect.  The
            // gate ensures a module that is unloaded never processes or logs chat again.
            _acceptMessages = false;
            _squadMessageListener?.Dispose();
            CloseDiagnosticLog(_rawMessageLogging?.Value == true);
            _diagnosticPanel?.Dispose();
        }

        private Task HandleSquadMessageAsync(SquadMessageInfo message, CancellationToken cancellationToken) {
            if (!_acceptMessages || cancellationToken.IsCancellationRequested) {
                return Task.CompletedTask;
            }

            // The bridge uses this message type for the Party/Squad chat family.  For the
            // spike, only retain Squad-family messages; subgroup data remains visible so
            // the field test can expose the known Party-vs-Squad classification caveat.
            if (message.ChannelType != ChannelType.Squad) {
                return Task.CompletedTask;
            }

            // Stopwatch.Elapsed is monotonic; storing its TimeSpan ticks avoids any dependency
            // on wall-clock changes while the field test is running.
            var capture = new CapturedSquadMessage(message, _uptime.Elapsed.Ticks);
            _pendingMessages.Enqueue(capture);
            Interlocked.Increment(ref _receivedCount);
            Interlocked.Exchange(ref _lastMessageReceivedTicks, capture.ReceivedTicks);

            if (_rawMessageLogging?.Value == true && !_diagnosticLogUnavailable) {
                _pendingDiagnosticLogLines.Enqueue(FormatDiagnosticLogLine(capture, message));
            }

            return Task.CompletedTask;
        }

        private void SynchronizeDiagnosticLog() {
            if (_rawMessageLogging?.Value != true) {
                CloseDiagnosticLog(false);
                _diagnosticLogUnavailable = false;
                DiscardPendingDiagnosticLogLines();
                return;
            }

            if (_diagnosticLogUnavailable) {
                return;
            }

            try {
                var logDirectory = ModuleParameters.DirectoriesManager.GetFullDirectoryPath(DiagnosticLogDirectoryName);
                var expectedPath = Path.Combine(logDirectory, $"CommanderTranslator-{DateTime.Now:yyyy-MM-dd}.log");

                if (_diagnosticLogWriter == null || !string.Equals(_diagnosticLogPath, expectedPath, StringComparison.OrdinalIgnoreCase)) {
                    CloseDiagnosticLog(true);
                    OpenDiagnosticLog(expectedPath);
                }

                WritePendingDiagnosticLogLines();
            } catch (Exception exception) {
                _diagnosticLogUnavailable = true;
                DiscardPendingDiagnosticLogLines();
                Logger.Warn("Unable to open the independent Commander Translator log file: {exception}", exception.ToString());
            }
        }

        private void OpenDiagnosticLog(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                throw new InvalidOperationException("The module Logs directory was not registered by Blish HUD.");
            }

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _diagnosticLogWriter = new StreamWriter(stream, new UTF8Encoding(false));
            _diagnosticLogPath = path;

            _diagnosticLogWriter.WriteLine();
            _diagnosticLogWriter.WriteLine($"# Commander Translator capture session started {DateTimeOffset.Now:O}");
            _diagnosticLogWriter.Flush();
            Logger.Info("Independent Commander Translator capture log enabled: {path}", path);
        }

        private void WritePendingDiagnosticLogLines() {
            if (_diagnosticLogWriter == null) {
                return;
            }

            while (_pendingDiagnosticLogLines.TryDequeue(out var line)) {
                _diagnosticLogWriter.WriteLine(line);
            }

            _diagnosticLogWriter.Flush();
        }

        private void CloseDiagnosticLog(bool flushPendingLines) {
            if (flushPendingLines) {
                WritePendingDiagnosticLogLines();
            } else {
                DiscardPendingDiagnosticLogLines();
            }

            if (_diagnosticLogWriter == null) {
                _diagnosticLogPath = null;
                return;
            }

            _diagnosticLogWriter.Dispose();
            _diagnosticLogWriter = null;
            _diagnosticLogPath = null;
        }

        private void DiscardPendingDiagnosticLogLines() {
            while (_pendingDiagnosticLogLines.TryDequeue(out _)) {
            }
        }

        private static string FormatDiagnosticLogLine(CapturedSquadMessage capture, SquadMessageInfo message) {
            return string.Format(
                CultureInfo.InvariantCulture,
                "[{0:O}] [CommanderTranslator Capture] localMs={1:F3} serverTimestamp={2:o} channelId={3} channelType={4} subgroup={5} broadcast={6} account={7} character={8} text={9}",
                DateTimeOffset.Now,
                capture.LocalMilliseconds,
                message.TimeStamp,
                message.ChannelId,
                message.ChannelType,
                message.Subgroup,
                message.IsBroadcast,
                SanitizeForSingleLine(message.AccountName),
                SanitizeForSingleLine(message.CharacterName),
                SanitizeForSingleLine(message.Text));
        }

        private void CreateDiagnosticPanel() {
            _diagnosticPanel = new FlowPanel {
                BackgroundColor = new Color(0, 0, 0, 190),
                ControlPadding = new Vector2(6, 4),
                FlowDirection = ControlFlowDirection.SingleTopToBottom,
                WidthSizingMode = SizingMode.Standard,
                HeightSizingMode = SizingMode.AutoSize,
                Width = 620,
                Location = new Point(40, 260),
                Parent = GameService.Graphics.SpriteScreen,
            };

            CreateLabel("Commander Translator — Squad Chat Capture Spike", Color.Gold);
            _connectionLabel = CreateLabel("Bridge: checking…", Color.Orange);
            _counterLabel = CreateLabel("Captured: 0 | Last message: never", Color.White);
            _lastMessageLabel = CreateLabel("Last: No Squad Chat captured yet.", Color.LightGray);
        }

        private Label CreateLabel(string text, Color color) {
            return new Label {
                Text = text,
                TextColor = color,
                Font = GameService.Content.DefaultFont16,
                ShowShadow = true,
                AutoSizeHeight = true,
                AutoSizeWidth = false,
                Width = 608,
                Parent = _diagnosticPanel,
            };
        }

        private void DrainCapturedMessages() {
            while (_pendingMessages.TryDequeue(out var message)) {
                _lastMessageSummary = FormatMessageSummary(message);
            }
        }

        private void RefreshDiagnosticPanel() {
            if (_diagnosticPanel == null) {
                return;
            }

            var bridgeRunning = GameService.ArcDpsV2.Running;
            var squadMessageAvailable = bridgeRunning && GameService.ArcDpsV2.IsMessageTypeAvailable(MessageType.SquadMessage);

            if (!bridgeRunning) {
                _connectionLabel.Text = "Bridge: waiting for ArcDPS + arcdps-bhud v2 connection";
                _connectionLabel.TextColor = Color.Orange;
            } else if (!squadMessageAvailable) {
                _connectionLabel.Text = "Bridge: connected, but SquadMessage is unavailable (check Unofficial Extras)";
                _connectionLabel.TextColor = Color.OrangeRed;
            } else {
                _connectionLabel.Text = "Bridge: connected | SquadMessage available";
                _connectionLabel.TextColor = Color.LimeGreen;
            }

            var count = Interlocked.Read(ref _receivedCount);
            var lastTicks = Interlocked.Read(ref _lastMessageReceivedTicks);
            var lastAge = lastTicks < 0
                ? "never"
                : FormatAge(TimeSpan.FromTicks(Math.Max(0, _uptime.Elapsed.Ticks - lastTicks)));

            _counterLabel.Text = $"Captured: {count} | Last message: {lastAge} ago";
            _lastMessageLabel.Text = $"Last: {_lastMessageSummary}";
        }

        private static string FormatMessageSummary(CapturedSquadMessage message) {
            var scope = message.Subgroup == byte.MaxValue ? "whole squad" : $"subgroup {message.Subgroup}";
            var broadcast = message.IsBroadcast ? " | broadcast" : string.Empty;
            var sender = string.IsNullOrWhiteSpace(message.CharacterName)
                ? message.AccountName
                : message.CharacterName;

            return $"[{scope}{broadcast}] {Truncate(SanitizeForSingleLine(sender), 48)}: {Truncate(SanitizeForSingleLine(message.Text), MaximumDisplayLength)}";
        }

        private static string FormatAge(TimeSpan age) {
            if (age.TotalSeconds < 1) {
                return "<1s";
            }

            if (age.TotalMinutes < 1) {
                return $"{Math.Floor(age.TotalSeconds)}s";
            }

            return $"{Math.Floor(age.TotalMinutes)}m";
        }

        private static string SanitizeForSingleLine(string value) {
            return string.IsNullOrWhiteSpace(value)
                ? "(unknown)"
                : value.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Truncate(string value, int maximumLength) {
            if (value.Length <= maximumLength) {
                return value;
            }

            return value.Substring(0, maximumLength - 1) + "…";
        }

        private readonly struct CapturedSquadMessage {
            public CapturedSquadMessage(SquadMessageInfo message, long receivedTicks) {
                ChannelId = message.ChannelId;
                Subgroup = message.Subgroup;
                IsBroadcast = message.IsBroadcast;
                AccountName = message.AccountName;
                CharacterName = message.CharacterName;
                Text = message.Text;
                ReceivedTicks = receivedTicks;
            }

            public uint ChannelId { get; }
            public byte Subgroup { get; }
            public bool IsBroadcast { get; }
            public string AccountName { get; }
            public string CharacterName { get; }
            public string Text { get; }
            public long ReceivedTicks { get; }
            public double LocalMilliseconds => TimeSpan.FromTicks(ReceivedTicks).TotalMilliseconds;
        }
    }
}
