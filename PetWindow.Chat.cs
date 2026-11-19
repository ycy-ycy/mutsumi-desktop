using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Mutsumi.Desktop
{
    internal partial class PetWindow
    {
        private const int ChatTimeoutSeconds = 100;
        private readonly Queue<ChatTurn> conversation = new Queue<ChatTurn>();
        private ChatInput chatInput;
        private ChatInput edgeNotice;
        private CancellationTokenSource chatRequest;
        private CancellationTokenSource switchRequest;
        private bool switchLineRequested;
        private DateTime readUntil;
        private string currentReply;
        private double replySeconds;
        private string chatDraft = "";

        private bool IsChatActive => chatInput != null || chatRequest != null || DateTime.UtcNow < readUntil;

        private void OpenChat()
        {
            if (suspended) return;
            if (chatInput != null)
            {
                chatInput.Activate();
                chatInput.Entry.Focus();
                return;
            }
            if (Mode == PetMode.Sleeping) SetMode(PetMode.Awake);
            if (Mode == PetMode.Awake) Rest();
            ShowComposer(true);
        }

        private void ShowComposer(bool activate)
        {
            speechTimeout.Stop();
            Bubble.Visibility = Visibility.Collapsed;
            CloseEdgeNotice();
            NoteInteraction();
            var input = new ChatInput(this, state.Identity, chatDraft,
                DateTime.UtcNow < readUntil ? currentReply : conversation.LastOrDefault()?.Reply, activate);
            chatInput = input;
            input.SetPending(chatRequest != null);
            input.RecallRequested += () =>
            {
                CancelRequest();
                input.SetPending(false);
                input.Entry.Focus();
            };
            input.Closed += (s, e) =>
            {
                if (chatInput != input) return;
                chatInput = null;
                chatDraft = input.Value;
                if (input.Submitted) SendChat(chatDraft);
                else { readUntil = DateTime.MinValue; NoteInteraction(); RefreshChat(); ScheduleIdle(); }
            };
            input.Show();
        }

        private async void SendChat(string text)
        {
            CancelSwitchComment();
            var identity = state.Identity;
            var request = new CancellationTokenSource();
            chatRequest = request;
            readUntil = DateTime.MinValue;
            currentReply = null;
            request.CancelAfter(TimeSpan.FromSeconds(ChatTimeoutSeconds));
            try
            {
                RefreshChat();
                PetReply reply = await PetChat.AskAsync(identity, text, conversation.ToArray(), request.Token);
                if (chatRequest != request) return;
                request.Token.ThrowIfCancellationRequested();
                reply.CommitMemory();
                var turn = new ChatTurn(text, identity, reply.Line);
                if (reply.Forgot) conversation.Clear();
                else
                {
                    if (conversation.Count == 4) conversation.Dequeue();
                    conversation.Enqueue(turn);
                }
                chatDraft = "";
                if (reply.Action != null && !suspended && !pressedButton.HasValue &&
                    state.Identity == identity && Mode == PetMode.Awake) Play(reply.Action, false);
                ShowChatReply(reply.Line, Math.Max(7, Math.Min(23, 3 + reply.Line.Length * .32)));
                if (reply.Forgot) switchLineRequested = false;
                else PrepareSwitchLines(turn);
            }
            catch (OperationCanceledException)
            {
                if (chatRequest == request)
                {
                    switchLineRequested = false;
                    ShowChatReply("等了有一会儿。右键可以重试刚才的话。", 9);
                }
            }
            catch (Exception ex)
            {
                if (chatRequest == request)
                {
                    switchLineRequested = false;
                    string message = request.IsCancellationRequested ? "回复超时，右键可以重试。" :
                        ex is InvalidOperationException ? ex.Message : "暂时无法读取 Codex 回复，右键可以重试。";
                    ShowChatReply(message, 11);
                }
            }
            finally
            {
                if (chatRequest == request) chatRequest = null;
                request.Dispose();
            }
        }

        private void CancelSwitchComment()
        {
            switchLineRequested = false;
            var request = switchRequest;
            switchRequest = null;
            request?.Cancel();
        }

        private async void PrepareSwitchLines(ChatTurn turn)
        {
            if (turn.PreparedLines != null) { ShowRequestedSwitchLine(turn); return; }
            if (switchRequest != null) return;
            var request = new CancellationTokenSource(TimeSpan.FromSeconds(ChatTimeoutSeconds));
            switchRequest = request;
            try
            {
                var lines = await PetChat.PrepareSwitchLinesAsync(turn, request.Token);
                if (switchRequest != request || request.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(lines.mutsumi) || string.IsNullOrWhiteSpace(lines.mortis)) return;
                turn.PreparedLines = lines;
                ShowRequestedSwitchLine(turn);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // An optional comment must never replace the user's answer with an error.
                System.Diagnostics.Debug.WriteLine("Switch comment: " + ex.GetType().Name);
            }
            finally
            {
                if (switchRequest == request) switchRequest = null;
                request.Dispose();
            }
        }

        private void ShowRequestedSwitchLine(ChatTurn turn)
        {
            if (!switchLineRequested || suspended || chatRequest != null || turn?.PreparedLines == null) return;
            switchLineRequested = false;
            var identity = state.Identity;
            var result = identity == Identity.Mortis ? turn.PreparedLines.mortis : turn.PreparedLines.mutsumi;
            string line = result.Trim().Split(new[] { '\r', '\n' }, 2)[0].Trim();
            var elements = new StringInfo(line);
            if (elements.LengthInTextElements > 90) line = elements.SubstringByTextElements(0, 89) + "…";
            if (chatInput != null)
            {
                chatInput.SwitchLine.Text = line;
                chatInput.SwitchLine.Visibility = Visibility.Visible;
            }
            else
            {
                bool answerVisible = DateTime.UtcNow < readUntil && currentReply != null;
                string speech = answerVisible ? currentReply + "\n" + line : line;
                double seconds = Math.Max(7, (readUntil - DateTime.UtcNow).TotalSeconds);
                ShowSpeech(speech, seconds);
            }
            turn.SwitchComment = Persona.For(identity).Name + "：" + line;
        }

        private void ShowChatReply(string line, double seconds)
        {
            chatRequest = null;
            currentReply = line;
            replySeconds = seconds;
            readUntil = suspended ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
            if (chatInput != null)
            {
                chatInput.Entry.Text = chatDraft;
                chatInput.SetPending(false);
                chatInput.SetAnswer(line);
            }
            RefreshChat();
        }

        // Repaint after a gesture using the same request or reply, at the pet's current position.
        private void RefreshChat()
        {
            if (suspended || chatInput != null) return;
            if (readUntil == DateTime.MaxValue) readUntil = DateTime.UtcNow.AddSeconds(replySeconds);
            bool waiting = chatRequest != null;
            double seconds = waiting ? ChatTimeoutSeconds + 1 : (readUntil - DateTime.UtcNow).TotalSeconds;
            if (seconds <= 0) return;
            string line = waiting ? "…" : currentReply;
            if (line.Length > 72 || line.Contains("\n") || line.Contains("\r")) { ShowComposer(false); return; }
            ShowSpeech(line, seconds);
        }

        private void ShowSpeech(string line, double seconds)
        {
            NoteInteraction();
            if (Mode != PetMode.Peeking) { Speak(line, seconds); return; }
            speechTimeout.Stop();
            CloseEdgeNotice();
            var notice = new ChatInput(this, state.Identity, "", line, activate: false, noticeOnly: true);
            edgeNotice = notice;
            notice.Closed += (s, e) => { if (edgeNotice == notice) edgeNotice = null; };
            notice.Show();
            speechTimeout.Interval = TimeSpan.FromSeconds(seconds);
            speechTimeout.Start();
        }

        private void CloseEdgeNotice()
        {
            edgeNotice?.Close();
        }

        private void CancelRequest()
        {
            CancelSwitchComment();
            var request = chatRequest;
            chatRequest = null;
            request?.Cancel();
            readUntil = DateTime.MinValue;
        }

        private void HideChatWindows()
        {
            CloseEdgeNotice();
            var input = chatInput;
            chatInput = null;
            if (input != null) chatDraft = input.Value;
            input?.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            CancelRequest();
            HideChatWindows();
            conversation.Clear();
            base.OnClosed(e);
        }
    }
}
