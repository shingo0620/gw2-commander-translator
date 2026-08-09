using System;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
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
        private const int MaximumDisplayLength = 220;

        private static readonly Logger Logger = Logger.GetLogger<CommanderTranslatorModule>();

        private readonly ConcurrentQueue<CapturedSquadMessage> _pendingMessages = new ConcurrentQueue<CapturedSquadMessage>();
        private readonly Stopwatch _uptime = Stopwatch.StartNew();

        private ArcDpsMessageListener<SquadMessageInfo> _squadMessageListener;
        private FlowPanel _diagnosticPanel;
        private Label _connectionLabel;
        private Label _counterLabel;
        private Label _lastMessageLabel;
        private SettingEntry<bool> _rawMessageLogging;
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
                () => "Record raw Squad Chat to the Blish HUD log",
                () => "Off by default. Enable only while testing; chat text, account names, and character names are written to the local Blish HUD log.");
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
            DrainCapturedMessages();
            RefreshDiagnosticPanel();
        }

        protected override void Unload() {
            // ArcDpsV2 can keep listener registrations for a future bridge reconnect.  The
            // gate ensures a module that is unloaded never processes or logs chat again.
            _acceptMessages = false;
            _squadMessageListener?.Dispose();
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

            if (_rawMessageLogging?.Value == true) {
                Logger.Info(
                    "[CommanderTranslator Capture] localMs={localMs} serverTimestamp={serverTimestamp:o} channelId={channelId} channelType={channelType} subgroup={subgroup} broadcast={broadcast} account={account} character={character} text={text}",
                    capture.LocalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                    message.TimeStamp,
                    message.ChannelId,
                    message.ChannelType,
                    message.Subgroup,
                    message.IsBroadcast,
                    SanitizeForSingleLine(message.AccountName),
                    SanitizeForSingleLine(message.CharacterName),
                    SanitizeForSingleLine(message.Text));
            }

            return Task.CompletedTask;
        }

        private void CreateDiagnosticPanel() {
            _diagnosticPanel = new FlowPanel {
                BackgroundColor = new Color(0, 0, 0, 190),
                ControlPadding = new Vector2(6, 4),
                FlowDirection = ControlFlowDirection.SingleTopToBottom,
                WidthSizingMode = SizingMode.Fixed,
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
                : FormatAge(TimeSpan.FromTicks(Math.Max(0, _uptime.ElapsedTicks - lastTicks)));

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
