﻿using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;
using Ryujinx.HLE.HOS.Services.Am.AppletAE;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Applets
{
    internal class SoftwareKeyboardApplet : IApplet
    {
        private const string DefaultText = "Ryujinx";
        private const long DebounceTimeMillis = 200;

        private readonly Switch _device;

        private const int StandardBufferSize    = 0x7D8;
        private const int InteractiveBufferSize = 0x7D4;

        private SoftwareKeyboardState _stateFg = SoftwareKeyboardState.Uninitialized;
        private InlineKeyboardState _stateBg = InlineKeyboardState.Uninitialized;
        private Object _stateLock = new Object();

        private bool _isBackground = false;

        private AppletSession _normalSession;
        private AppletSession _interactiveSession;

        // Configuration for foreground mode
        private SoftwareKeyboardConfig  _keyboardFgConfig;
        private SoftwareKeyboardCalc    _keyboardBgCalc;
        private SoftwareKeyboardDictSet _keyboardBgDict;

        // Configuration for background mode
        private SoftwareKeyboardInitialize _keyboardBgInitialize;

        private byte[] _transferMemory;

        private string   _textValue = "";
        private bool     _okPressed = false;
        private Encoding _encoding  = Encoding.Unicode;
        private long     _lastTextSetMillis = 0;

        public event EventHandler AppletStateChanged;

        public SoftwareKeyboardApplet(Horizon system)
        {
            _device = system.Device;
        }

        public ResultCode Start(AppletSession normalSession,
                                AppletSession interactiveSession)
        {
            _normalSession      = normalSession;
            _interactiveSession = interactiveSession;

            _interactiveSession.DataAvailable += OnInteractiveData;

            var launchParams   = _normalSession.Pop();
            var keyboardConfig = _normalSession.Pop();

            // TODO: A better way would be handling the background creation properly
            // in LibraryAppleCreator / Acessor instead of guessing by size.
            if (keyboardConfig.Length == Marshal.SizeOf<SoftwareKeyboardInitialize>())
            {
                _isBackground = true;

                _keyboardBgInitialize = ReadStruct<SoftwareKeyboardInitialize>(keyboardConfig);
                _stateBg = InlineKeyboardState.Uninitialized;

                // Start a task with the GUI handler to get user's input.
                /*new Task(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(200);
                        _interactiveSession.Push(InlineResponses.Default(GetInlineState()));
                    }
                }).Start();*/

                return ResultCode.Success;
            }
            else
            {
                _isBackground = false;

                if (keyboardConfig.Length < Marshal.SizeOf<SoftwareKeyboardConfig>())
                {
                    Logger.Error?.Print(LogClass.ServiceAm, $"SoftwareKeyboardConfig size mismatch. Expected {Marshal.SizeOf<SoftwareKeyboardConfig>():x}. Got {keyboardConfig.Length:x}");
                }
                else
                {
                    _keyboardFgConfig = ReadStruct<SoftwareKeyboardConfig>(keyboardConfig);
                }

                if (!_normalSession.TryPop(out _transferMemory))
                {
                    Logger.Error?.Print(LogClass.ServiceAm, "SwKbd Transfer Memory is null");
                }

                if (_keyboardFgConfig.UseUtf8)
                {
                    _encoding = Encoding.UTF8;
                }

                _stateFg = SoftwareKeyboardState.Ready;

                ExecuteForegroundKeyboard();

                return ResultCode.Success;
            }
        }

        public ResultCode GetResult()
        {
            Logger.Info?.Print(LogClass.ServiceAm, $"Keyboard exiting");

            return ResultCode.Success;
        }

        private InlineKeyboardState GetInlineState()
        {
            lock (_stateLock)
            {
                return _stateBg;
            }
        }

        private void SetInlineState(InlineKeyboardState state)
        {
            lock (_stateLock)
            {
                _stateBg = state;
            }
        }

        private void ExecuteForegroundKeyboard()
        {
            string initialText = null;

            // Initial Text is always encoded as a UTF-16 string in the work buffer (passed as transfer memory)
            // InitialStringOffset points to the memory offset and InitialStringLength is the number of UTF-16 characters
            if (_transferMemory != null && _keyboardFgConfig.InitialStringLength > 0)
            {
                initialText = Encoding.Unicode.GetString(_transferMemory, _keyboardFgConfig.InitialStringOffset, 2 * _keyboardFgConfig.InitialStringLength);
            }

            // If the max string length is 0, we set it to a large default
            // length.
            if (_keyboardFgConfig.StringLengthMax == 0)
            {
                _keyboardFgConfig.StringLengthMax = 100;
            }

            var args = new SoftwareKeyboardUiArgs
            {
                HeaderText = _keyboardFgConfig.HeaderText,
                SubtitleText = _keyboardFgConfig.SubtitleText,
                GuideText = _keyboardFgConfig.GuideText,
                SubmitText = (!string.IsNullOrWhiteSpace(_keyboardFgConfig.SubmitText) ? _keyboardFgConfig.SubmitText : "OK"),
                StringLengthMin = _keyboardFgConfig.StringLengthMin,
                StringLengthMax = _keyboardFgConfig.StringLengthMax,
                InitialText = initialText
            };

            // Call the configured GUI handler to get user's input
            if (_device.UiHandler == null)
            {
                Logger.Warning?.Print(LogClass.Application, "GUI Handler is not set. Falling back to default");
                _okPressed = true;
            }
            else
            {
                _okPressed = _device.UiHandler.DisplayInputDialog(args, out _textValue);
            }

            _textValue ??= initialText ?? DefaultText;

            // If the game requests a string with a minimum length less
            // than our default text, repeat our default text until we meet
            // the minimum length requirement.
            // This should always be done before the text truncation step.
            while (_textValue.Length < _keyboardFgConfig.StringLengthMin)
            {
                _textValue = String.Join(" ", _textValue, _textValue);
            }

            // If our default text is longer than the allowed length,
            // we truncate it.
            if (_textValue.Length > _keyboardFgConfig.StringLengthMax)
            {
                _textValue = _textValue.Substring(0, (int)_keyboardFgConfig.StringLengthMax);
            }

            // Does the application want to validate the text itself?
            if (_keyboardFgConfig.CheckText)
            {
                // The application needs to validate the response, so we
                // submit it to the interactive output buffer, and poll it
                // for validation. Once validated, the application will submit
                // back a validation status, which is handled in OnInteractiveDataPushIn.
                _stateFg = SoftwareKeyboardState.ValidationPending;

                _interactiveSession.Push(BuildResponse(_textValue, true));
            }
            else
            {
                // If the application doesn't need to validate the response,
                // we push the data to the non-interactive output buffer
                // and poll it for completion.
                _stateFg = SoftwareKeyboardState.Complete;

                _normalSession.Push(BuildResponse(_textValue, false));

                AppletStateChanged?.Invoke(this, null);
            }
        }

        private void OnInteractiveData(object sender, EventArgs e)
        {
            // Obtain the validation status response.
            var data = _interactiveSession.Pop();

            if (_isBackground)
            {
                OnBackgroundInteractiveData(data);
            }
            else
            {
                OnForegroundInteractiveData(data);
            }
        }

        private void OnForegroundInteractiveData(byte[] data)
        {
            if (_stateFg == SoftwareKeyboardState.ValidationPending)
            {
                // TODO(jduncantor):
                // If application rejects our "attempt", submit another attempt,
                // and put the applet back in PendingValidation state.

                // For now we assume success, so we push the final result
                // to the standard output buffer and carry on our merry way.
                _normalSession.Push(BuildResponse(_textValue, false));

                AppletStateChanged?.Invoke(this, null);

                _stateFg = SoftwareKeyboardState.Complete;
            }
            else if(_stateFg == SoftwareKeyboardState.Complete)
            {
                // If we have already completed, we push the result text
                // back on the output buffer and poll the application.
                _normalSession.Push(BuildResponse(_textValue, false));

                AppletStateChanged?.Invoke(this, null);
            }
            else
            {
                // We shouldn't be able to get here through standard swkbd execution.
                throw new InvalidOperationException("Software Keyboard is in an invalid state.");
            }
        }

        private void OnBackgroundInteractiveData(byte[] data)
        {
            // WARNING: Only invoke applet state changes after an explicit finalization
            // request from the game, this is because the inline keyboard is expected to
            // keep running in the background sending data by itself.

            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                var request = (InlineKeyboardRequest)reader.ReadUInt32();

                Logger.Info?.Print(LogClass.ServiceAm, $"Keyboard received command {request}");

                long remaining;

                InlineKeyboardState state = GetInlineState();

                // Always show the keyboard if the state is 'Ready'.
                bool showKeyboard = state == InlineKeyboardState.Unknown2;

                switch (request)
                {
                    case InlineKeyboardRequest.UseChangedStringV2:
                        // Not used because we only send the entire string after confirmation.
                        //_interactiveSession.Push(InlineResponses.Default(state));
                        break;
                    case InlineKeyboardRequest.UseMovedCursorV2:
                        // Not used because we only send the entire string after confirmation.
                        //_interactiveSession.Push(InlineResponses.Default(state));
                        break;
                    case InlineKeyboardRequest.SetUserWordInfo:
                        // TODO: May be used in the future for word suggestions.
                        //_interactiveSession.Push(InlineResponses.FinishedInitialize(InlineKeyboardState.Initialized));
                        _interactiveSession.Push(InlineResponses.ReleasedUserWordInfo(state));
                        break;
                    case InlineKeyboardRequest.SetCustomizeDic:
                        remaining = stream.Length - stream.Position;
                        if (remaining != Marshal.SizeOf<SoftwareKeyboardDictSet>())
                        {
                            Logger.Error?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard DictSet of {remaining} bytes!");
                        }
                        else
                        {
                            var keyboardDictData = reader.ReadBytes((int)remaining);
                            _keyboardBgDict = ReadStruct<SoftwareKeyboardDictSet>(keyboardDictData);
                        }
                        //_interactiveSession.Push(InlineResponses.Default(state));
                        _interactiveSession.Push(InlineResponses.FinishedInitialize(InlineKeyboardState.Initialized));
                        break;
                    case InlineKeyboardRequest.Calc:
                        // Put the keyboard in a Ready state, this will force showing
                        remaining = stream.Length - stream.Position;
                        if (remaining != Marshal.SizeOf<SoftwareKeyboardCalc>())
                        {
                            Logger.Error?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard Calc of {remaining} bytes!");
                        }
                        else
                        {
                            var keyboardCalcData = reader.ReadBytes((int)remaining);
                            _keyboardBgCalc = ReadStruct<SoftwareKeyboardCalc>(keyboardCalcData);

                            if (_keyboardBgCalc.Utf8Mode == 0x1)
                            {
                                _encoding = Encoding.UTF8;
                            }

                            // Force showing the keyboard regardless of the state, an unwanted
                            // input dialog may show, but it is better than a soft lock.
                            if (_keyboardBgCalc.Appear.ShouldBeHidden == 0)
                            {
                                showKeyboard = true;
                            }
                        }
                        // Foo
                        // Reset the cursor.
                        InlineKeyboardState newstate = InlineKeyboardState.DataAvailable;
                        SetInlineState(newstate);
                        if (_encoding == Encoding.UTF8)
                        {
                            _interactiveSession.Push(InlineResponses.MovedCursorUtf8("", state));
                        }
                        else
                        {
                            _interactiveSession.Push(InlineResponses.MovedCursor("", state));
                        }
                        /*if (!showKeyboard)
                        {
                            state = InlineKeyboardState.Unknown2;
                            SetInlineState(state);
                            _interactiveSession.Push(InlineResponses.Default(state));
                            break;
                        }*/
                        // Send an initialization finished signal.
                        state = InlineKeyboardState.Unknown2;
                        SetInlineState(state);
                        _interactiveSession.Push(InlineResponses.FinishedInitialize(state));
                        // Start a task with the GUI handler to get user's input.
                        new Task(() => { SendInputText(showKeyboard, state); }).Start();
                        break;
                    case InlineKeyboardRequest.Finalize:
                        // The game wants to close the keyboard applet and will wait for a state change.
                        _stateBg = InlineKeyboardState.Uninitialized;
                        AppletStateChanged?.Invoke(this, null);
                        break;
                    default:
                        // We shouldn't be able to get here through standard swkbd execution.
                        Logger.Error?.Print(LogClass.ServiceAm, $"Invalid Software Keyboard request {request} during state {_stateBg}!");
                        //_interactiveSession.Push(InlineResponses.Default(state));
                        break;
                }
            }
        }

        private void SendInputText(bool showKeyboard, InlineKeyboardState oldState)
        {
            bool submit = true;
            string inputText = (!string.IsNullOrWhiteSpace(_keyboardBgCalc.InputText) ? _keyboardBgCalc.InputText : DefaultText);

            long currentMillis = PerformanceCounter.ElapsedMilliseconds;
            long inputElapsedMillis = currentMillis - _lastTextSetMillis;

            if (inputElapsedMillis < DebounceTimeMillis)
            {
                // Debounce repeating the value submitted.
                inputText = _textValue;

                Logger.Info?.Print(LogClass.Application, "Debouncing keyboard request");
            }
            else if (!showKeyboard)
            {
                // Submit the default text to avoid soft locking if the keyboard was ignored by
                // accident. It's better to change the name than being locked out of the game.
                inputText = DefaultText;

                Logger.Info?.Print(LogClass.Application, "Received a dummy Calc, keyboard will not be shown");
            }
            else if (_device.UiHandler == null)
            {
                Logger.Warning?.Print(LogClass.Application, "GUI Handler is not set. Falling back to default");
            }
            else
            {
                // Call the configured GUI handler to get user's input.
                var args = new SoftwareKeyboardUiArgs
                {
                    HeaderText = "", // The inline keyboard lacks these texts
                    SubtitleText = "",
                    GuideText = "",
                    SubmitText = (!string.IsNullOrWhiteSpace(_keyboardBgCalc.Appear.OkText) ? _keyboardBgCalc.Appear.OkText : "OK"),
                    StringLengthMin = 0,
                    StringLengthMax = 100,
                    InitialText = inputText
                };

                submit = _device.UiHandler.DisplayInputDialog(args, out inputText);
            }

            // Change state to complete once data is available.
            InlineKeyboardState newState = InlineKeyboardState.Complete;

            if (submit)
            {
                Logger.Debug?.Print(LogClass.ServiceAm, "Sending keyboard OK...");

                if (_encoding == Encoding.UTF8)
                {
                    _interactiveSession.Push(InlineResponses.MovedCursorUtf8(inputText, oldState));
                    _interactiveSession.Push(InlineResponses.DecidedEnterUtf8(inputText, newState));
                }
                else
                {
                    _interactiveSession.Push(InlineResponses.MovedCursor(inputText, oldState));
                    _interactiveSession.Push(InlineResponses.DecidedEnter(inputText, newState));
                }
            }
            else
            {
                Logger.Debug?.Print(LogClass.ServiceAm, "Sending keyboard Cancel...");
                _interactiveSession.Push(InlineResponses.DecidedCancel(newState));
            }

            _interactiveSession.Push(InlineResponses.Default(newState));

            // Debounce the response.
            // TODO: Why is this necessary? Does the software expect a constant stream of responses?

            Thread.Sleep(500);

            Logger.Debug?.Print(LogClass.ServiceAm, $"Resetting state of the keyboard to {newState}...");
            
            newState = InlineKeyboardState.Initialized;
            SetInlineState(newState);
            _interactiveSession.Push(InlineResponses.Default(newState));
            _textValue = inputText;
            _lastTextSetMillis = PerformanceCounter.ElapsedMilliseconds;
        }

        private byte[] BuildResponse(string text, bool interactive)
        {
            int bufferSize = interactive ? InteractiveBufferSize : StandardBufferSize;

            using (MemoryStream stream = new MemoryStream(new byte[bufferSize]))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                byte[] output = _encoding.GetBytes(text);

                if (!interactive)
                {
                    // Result Code
                    writer.Write(_okPressed ? 0U : 1U);
                }
                else
                {
                    // In interactive mode, we write the length of the text as a long, rather than
                    // a result code. This field is inclusive of the 64-bit size.
                    writer.Write((long)output.Length + 8);
                }

                writer.Write(output);

                return stream.ToArray();
            }
        }

        private static T ReadStruct<T>(byte[] data)
            where T : struct
        {
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);

            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
