﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information

using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace System.Device.Gpio.Drivers
{
    public class LibGpiodDriver : UnixDriver
    {
        private static string s_consumerName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        private readonly object _pinNumberLock;
        private readonly ConcurrentDictionary<int, SafeLineHandle> _pinNumberToSafeLineHandle;
        private readonly ConcurrentDictionary<int, LibGpiodDriverEventHandler> _pinNumberToEventHandler;
        private readonly int _pinCount;
        private SafeChipHandle _chip;

        protected internal override int PinCount => _pinCount;

        public LibGpiodDriver(int gpioChip = 0)
        {
            try
            {
                _pinNumberLock = new object();
                _chip = Interop.libgpiod.gpiod_chip_open_by_number(gpioChip);
                if (_chip == null)
                {
                    throw ExceptionHelper.GetIOException(ExceptionResource.NoChipFound, Marshal.GetLastWin32Error());
                }

                _pinCount = Interop.libgpiod.gpiod_chip_num_lines(_chip);
                _pinNumberToEventHandler = new ConcurrentDictionary<int, LibGpiodDriverEventHandler>();
                _pinNumberToSafeLineHandle = new ConcurrentDictionary<int, SafeLineHandle>();
            }
            catch (DllNotFoundException)
            {
                throw ExceptionHelper.GetPlatformNotSupportedException(ExceptionResource.LibGpiodNotInstalled);
            }
        }

        protected internal override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
        {
            if ((eventTypes & PinEventTypes.Rising) != 0 || (eventTypes & PinEventTypes.Falling) != 0)
            {
                LibGpiodDriverEventHandler eventHandler = _pinNumberToEventHandler.GetOrAdd(pinNumber, PopulateEventHandler);

                if ((eventTypes & PinEventTypes.Rising) != 0)
                {
                    eventHandler.ValueRising += callback;
                }

                if ((eventTypes & PinEventTypes.Falling) != 0)
                {
                    eventHandler.ValueFalling += callback;
                }
            }
            else
            {
                throw ExceptionHelper.GetArgumentException(ExceptionResource.InvalidEventType);
            }
        }

        private LibGpiodDriverEventHandler PopulateEventHandler(int pinNumber)
        {
            lock (_pinNumberLock)
            {
                if (_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out SafeLineHandle pinHandle))
                {
                    if (!Interop.libgpiod.gpiod_line_is_free(pinHandle))
                    {
                        pinHandle.Dispose();
                        pinHandle = Interop.libgpiod.gpiod_chip_get_line(_chip, pinNumber);
                        _pinNumberToSafeLineHandle[pinNumber] = pinHandle;
                    }
                }

                return new LibGpiodDriverEventHandler(pinNumber, pinHandle);
            }
        }

        protected internal override void ClosePin(int pinNumber)
        {
            lock (_pinNumberLock)
            {
                if (_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out SafeLineHandle pinHandle) &&
                    !IsListeningEvent(pinNumber))
                {
                    pinHandle?.Dispose();
                    // We know this works
                    _pinNumberToSafeLineHandle.TryRemove(pinNumber, out _);
                }
            }
        }

        private bool IsListeningEvent(int pinNumber)
        {
            return _pinNumberToEventHandler.ContainsKey(pinNumber);
        }

        protected internal override int ConvertPinNumberToLogicalNumberingScheme(int pinNumber) =>
            throw ExceptionHelper.GetPlatformNotSupportedException(ExceptionResource.ConvertPinNumberingSchemaError);

        protected internal override PinMode GetPinMode(int pinNumber)
        {
            lock (_pinNumberLock)
            {
                if (!_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out SafeLineHandle pinHandle))
                {
                    throw ExceptionHelper.GetInvalidOperationException(ExceptionResource.PinNotOpenedError,
                        pin: pinNumber);
                }

                return pinHandle.PinMode;
            }
        }

        protected internal override bool IsPinModeSupported(int pinNumber, PinMode mode)
        {
            // Libgpiod Api do not support pull up or pull down resistors for now.
            return mode != PinMode.InputPullDown && mode != PinMode.InputPullUp;
        }

        protected internal override void OpenPin(int pinNumber)
        {
            lock (_pinNumberLock)
            {
                if (_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out _))
                {
                    return;
                }

                SafeLineHandle pinHandle = Interop.libgpiod.gpiod_chip_get_line(_chip, pinNumber);
                if (pinHandle == null)
                {
                    throw ExceptionHelper.GetIOException(ExceptionResource.OpenPinError, Marshal.GetLastWin32Error());
                }

                _pinNumberToSafeLineHandle.TryAdd(pinNumber, pinHandle);
            }
        }

        protected internal override PinValue Read(int pinNumber)
        {
            if (_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out SafeLineHandle pinHandle))
            {
                int result = Interop.libgpiod.gpiod_line_get_value(pinHandle);
                if (result == -1)
                {
                        throw ExceptionHelper.GetIOException(ExceptionResource.ReadPinError, Marshal.GetLastWin32Error(), pinNumber);
                }

                return result;
            }

            throw ExceptionHelper.GetInvalidOperationException(ExceptionResource.PinNotOpenedError, pin: pinNumber);
        }

        protected internal override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
        {
            if (_pinNumberToEventHandler.TryGetValue(pinNumber, out LibGpiodDriverEventHandler eventHandler))
            {
                eventHandler.ValueFalling -= callback;
                eventHandler.ValueRising -= callback;
                if (eventHandler.IsCallbackListEmpty())
                {
                    _pinNumberToEventHandler.TryRemove(pinNumber, out eventHandler);
                    eventHandler.Dispose();
                }
            }
            else
            {
                throw ExceptionHelper.GetInvalidOperationException(ExceptionResource.NotListeningForEventError);
            }
        }

        protected internal override void SetPinMode(int pinNumber, PinMode mode)
        {
            int requestResult = -1;
            if (_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out SafeLineHandle pinHandle))
            {
                if (mode == PinMode.Input)
                {
                        requestResult = Interop.libgpiod.gpiod_line_request_input(pinHandle, s_consumerName);
                }
                else
                {
                        requestResult = Interop.libgpiod.gpiod_line_request_output(pinHandle, s_consumerName);
                }

                pinHandle.PinMode = mode;
            }

            if (requestResult == -1)
            {
                throw ExceptionHelper.GetIOException(ExceptionResource.SetPinModeError, Marshal.GetLastWin32Error(), pinNumber);
            }
        }

        protected internal override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
        {
            if ((eventTypes & PinEventTypes.Rising) != 0 || (eventTypes & PinEventTypes.Falling) != 0)
            {
                LibGpiodDriverEventHandler eventHandler = _pinNumberToEventHandler.GetOrAdd(pinNumber, PopulateEventHandler);

                if ((eventTypes & PinEventTypes.Rising) != 0)
                {
                    eventHandler.ValueRising += Callback;
                }

                if ((eventTypes & PinEventTypes.Falling) != 0)
                {
                    eventHandler.ValueFalling += Callback;
                }

                bool eventOccurred = false;
                PinEventTypes typeOfEventOccured = PinEventTypes.None;
                void Callback(object o, PinValueChangedEventArgs e)
                {
                    eventOccurred = true;
                    typeOfEventOccured = e.ChangeType;
                }

                WaitForEventResult(cancellationToken, eventHandler.CancellationTokenSource.Token, ref eventOccurred);
                RemoveCallbackForPinValueChangedEvent(pinNumber, Callback);

                return new WaitForEventResult
                {
                    TimedOut = !eventOccurred,
                    EventTypes = eventOccurred ? typeOfEventOccured : PinEventTypes.None,
                };
            }
            else
            {
                throw ExceptionHelper.GetArgumentException(ExceptionResource.InvalidEventType);
            }
        }

        private void WaitForEventResult(CancellationToken sourceToken, CancellationToken parentToken, ref bool eventOccurred)
        {
            while (!(sourceToken.IsCancellationRequested || parentToken.IsCancellationRequested || eventOccurred))
            {
                Thread.Sleep(1);
            }
        }

        protected internal override void Write(int pinNumber, PinValue value)
        {
            if (!_pinNumberToSafeLineHandle.TryGetValue(pinNumber, out SafeLineHandle pinHandle))
            {
                throw ExceptionHelper.GetInvalidOperationException(ExceptionResource.PinNotOpenedError,
                    pin: pinNumber);
            }

            Interop.libgpiod.gpiod_line_set_value(pinHandle, (value == PinValue.High) ? 1 : 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (_pinNumberToEventHandler != null)
            {
                foreach (KeyValuePair<int, LibGpiodDriverEventHandler> kv in _pinNumberToEventHandler)
                {
                    LibGpiodDriverEventHandler eventHandler = kv.Value;
                    eventHandler.Dispose();
                }

                _pinNumberToEventHandler.Clear();
            }

            if (_pinNumberToSafeLineHandle != null)
            {
                foreach (int pin in _pinNumberToSafeLineHandle.Keys)
                {
                    if (_pinNumberToSafeLineHandle.TryGetValue(pin, out SafeLineHandle pinHandle))
                    {
                        pinHandle?.Dispose();
                    }
                }

                _pinNumberToSafeLineHandle.Clear();
            }

            _chip?.Dispose();
            _chip = null;

            base.Dispose(disposing);
        }
    }
}
