using System;
using System.Collections.Generic;
using System.Threading;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace Ds18Bridge
{
    /// <summary>
    /// USB transport for the DS18 DP12 DSP.
    ///
    /// Endpoint, interface, configuration and timeouts are taken from the original app's
    /// UsbInterface class (AP.cs): VID 0x1483 / PID 0x1095, configuration 1, interface 0,
    /// EP1 OUT (0x01) for TX, EP1 IN (0x81) for RX, 500 ms write timeout, 64-byte reads.
    ///
    /// Framing/CRC come from Protocol.cs, which is a byte-exact port of PackUsbData/CRC_Get.
    /// </summary>
    public sealed class DspClient : IDisposable
    {
        public const int VendorId = 0x1483;
        public const int ProductId = 0x1095;

        private const int WriteTimeoutMs = 500;
        private const int ReadChunk = 64;
        private const int ReadTimeoutMs = 250;

        private readonly object _sync = new object();
        private readonly List<byte> _pending = new List<byte>(4096);

        private UsbDevice _dev;
        private UsbEndpointWriter _writer;
        private UsbEndpointReader _reader;
        private Thread _thread;
        private volatile bool _running;

        public string LastError = "";

        // Set by Send on any transport-level failure. A stale handle after a DSP
        // power-cycle still reports IsOpen, so IsOpen must also consider the last
        // write: this is what lets Connect and the link watcher notice a dead link
        // instead of pretending everything is fine.
        private volatile bool _broken;

        public bool IsOpen
        {
            get { return _dev != null && _dev.IsOpen && !_broken; }
        }

        /// <summary>Fires for every successfully framed reply, including unsolicited ones.</summary>
        public event Action<RxFrame> FrameReceived;

        /// <summary>True once an endpoint writer exists (device bound and claimed).</summary>
        public bool Open()
        {
            lock (_sync)
            {
                if (IsOpen)
                {
                    return true;
                }

                // A previous link may have died without being released (broken flag set,
                // or half-closed). Drop the leftovers so this open is a true reopen.
                if (_dev != null)
                {
                    try { ReleaseDevice(); }
                    catch { }
                }

                try
                {
                    UsbDeviceFinder finder = new UsbDeviceFinder(VendorId, ProductId);

                    // LibUsbDotNet returns null for BOTH "not plugged in" and "plugged in but
                    // could not be opened", which produced a misleading "device not found" when
                    // the real fault was a stopped libusbDspAi driver. Check enumeration first
                    // so the message tells the operator which side to look at.
                    UsbRegistry reg = null;
                    try { reg = UsbDevice.AllDevices.Find(finder); }
                    catch { }

                    if (reg == null)
                    {
                        LastError = "device not enumerated (VID_1483&PID_1095) - check the amp is "
                                  + "powered on and that the USB cable carries data (charge-only "
                                  + "cables have no data lines)";
                        return false;
                    }

                    UsbDevice dev = null;
                    try { reg.Open(out dev); }
                    catch (Exception ex)
                    {
                        LastError = "device is present but could not be opened: " + ex.Message;
                        return false;
                    }
                    if (dev == null)
                    {
                        LastError = "device is plugged in but could not be opened - the "
                                  + "libusbDspAi driver is probably stopped; unplug the DSP, wait "
                                  + "3 seconds and plug it back in (or another process holds it)";
                        return false;
                    }

                    IUsbDevice idev = dev as IUsbDevice;
                    if (idev != null)
                    {
                        idev.SetConfiguration(1);
                        idev.ClaimInterface(0);
                    }

                    _dev = dev;
                    _writer = dev.OpenEndpointWriter(WriteEndpointID.Ep01);
                    _reader = dev.OpenEndpointReader(ReadEndpointID.Ep01, ReadChunk);
                    _reader.ReadFlush();

                    _pending.Clear();
                    _running = true;
                    _thread = new Thread(ReadLoop);
                    _thread.IsBackground = true;
                    _thread.Name = "ds18-rx";
                    _thread.Start();

                    _broken = false;
                    LastError = "";
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "open failed: " + ex.Message;
                    ReleaseDevice();
                    return false;
                }
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                ReleaseDevice();
            }
        }

        /// <summary>Writes one already-framed buffer to EP1 OUT.</summary>
        public bool Send(byte[] frame)
        {
            if (_writer == null)
            {
                LastError = "device not open";
                return false;
            }

            try
            {
                int transferred;
                ErrorCode ec = _writer.Write(frame, 0, frame.Length, WriteTimeoutMs, out transferred);
                if (ec != ErrorCode.None)
                {
                    _broken = true;   // transport failure: this handle is dead, not busy
                    LastError = "write error: " + ec;
                    return false;
                }
                if (transferred != frame.Length)
                {
                    _broken = true;
                    LastError = "short write: " + transferred + "/" + frame.Length;
                    return false;
                }
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                LastError = "write failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Waits up to timeoutMs for the next well-formed reply. Leading garbage and partial
        /// frames are retained/trimmed automatically by the receive thread.
        /// </summary>
        public bool TryReceive(out RxFrame frame, int timeoutMs)
        {
            frame = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (true)
            {
                lock (_sync)
                {
                    if (_pending.Count > 0)
                    {
                        byte[] arr = _pending.ToArray();
                        RxFrame parsed;
                        int consumed;
                        if (RxParser.TryParse(arr, arr.Length, out parsed, out consumed))
                        {
                            _pending.RemoveRange(0, consumed);
                            frame = parsed;
                            return true;
                        }
                        TrimLocked();
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }
                Thread.Sleep(5);
            }
        }

        /// <summary>Send a frame and wait for the first well-formed reply.</summary>
        public bool Request(byte[] frame, int timeoutMs, out RxFrame reply)
        {
            reply = null;
            if (!Send(frame))
            {
                return false;
            }
            return TryReceive(out reply, timeoutMs);
        }

        /// <summary>
        /// Send and wait specifically for the reply whose head cmdIndex matches expectedCmd.
        /// Other replies seen while waiting are surfaced through FrameReceived so no data is lost.
        /// </summary>
        public bool RequestCmd(byte[] frame, byte expectedCmd, int timeoutMs, out RxFrame reply)
        {
            reply = null;
            if (!Send(frame))
            {
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                RxFrame f;
                if (!TryReceive(out f, 50))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        LastError = "timeout waiting for reply cmd " + expectedCmd;
                        return false;
                    }
                    continue;
                }
                if (f.CmdIndex == expectedCmd)
                {
                    reply = f;
                    LastError = "";
                    return true;
                }
                Raise(f);
            }
        }

        public void Dispose()
        {
            Close();
        }

        private void Raise(RxFrame frame)
        {
            Action<RxFrame> handler = FrameReceived;
            if (handler != null)
            {
                try { handler(frame); }
                catch { }
            }
        }

        private void ReadLoop()
        {
            byte[] chunk = new byte[ReadChunk];

            while (_running)
            {
                try
                {
                    int transferred;
                    ErrorCode ec = _reader.Read(chunk, 0, chunk.Length, ReadTimeoutMs, out transferred);
                    if (ec == ErrorCode.None && transferred > 0)
                    {
                        lock (_sync)
                        {
                            for (int i = 0; i < transferred; i++)
                            {
                                _pending.Add(chunk[i]);
                            }
                            TrimLocked();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        LastError = "read failed: " + ex.Message;
                        Thread.Sleep(100);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Drops bytes before the first plausible 0x55 0xAA sync so a partial frame that
        /// straddles two 64-byte reads is preserved.
        /// </summary>
        private void TrimLocked()
        {
            int first = -1;
            for (int i = 0; i + 2 <= _pending.Count; i++)
            {
                if (_pending[i] == 0x55 && _pending[i + 1] == 0xAA)
                {
                    first = i;
                    break;
                }
            }

            if (first > 0)
            {
                _pending.RemoveRange(0, first);
            }
            else if (first < 0 && _pending.Count > 1)
            {
                // No sync at all: keep the final byte, it may be the 0x55 of a split sync word.
                _pending.RemoveRange(0, _pending.Count - 1);
            }
        }

        private void ReleaseDevice()
        {
            _running = false;

            Thread t = _thread;
            _thread = null;
            if (t != null && t.IsAlive && t != Thread.CurrentThread)
            {
                try { t.Join(600); }
                catch { }
            }

            try { if (_writer != null) _writer.Dispose(); }
            catch { }
            _writer = null;

            try { if (_reader != null) _reader.Dispose(); }
            catch { }
            _reader = null;

            try
            {
                if (_dev != null)
                {
                    IUsbDevice idev = _dev as IUsbDevice;
                    if (idev != null)
                    {
                        try { idev.ReleaseInterface(0); }
                        catch { }
                    }
                    _dev.Close();
                }
            }
            catch { }
            _dev = null;

            try { UsbDevice.Exit(); }
            catch { }
        }
    }
}
