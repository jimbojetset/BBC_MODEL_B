// ============================================================================
// Project:     BBC
// File:        CassetteInterface.cs
// Description: Minimal BBC cassette/serial ACIA state.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{
    /// <summary>
    /// Models enough of the BBC Micro cassette/serial hardware for software that
    /// probes whether a tape is still running after being converted to disc.
    /// </summary>
    public sealed class CassetteInterface
    {
        private const ushort AciaStart = 0xFE08;
        private const ushort AciaEnd = 0xFE0B;
        private const ushort SerialUlaStart = 0xFE10;
        private const ushort SerialUlaEnd = 0xFE17;

        private const byte AciaStatusReceiveDataFull = 0x01;
        private const byte AciaStatusTransmitDataEmpty = 0x02;
        private const byte AciaStatusDataCarrierDetect = 0x04;
        private const byte AciaStatusClearToSend = 0x08;

        private byte serialUlaControl;

        /// <summary>
        /// Gets or sets whether the emulated cassette deck motor is running.
        /// </summary>
        public bool MotorRunning { get; private set; }

        /// <summary>
        /// Gets or sets whether cassette input data is currently available.
        /// </summary>
        public bool TapePlaying { get; private set; }

        public static bool IsAddress(ushort address)
        {
            return address is >= AciaStart and <= AciaEnd
                || address is >= SerialUlaStart and <= SerialUlaEnd;
        }

        public byte Read(ushort address)
        {
            if (address is >= AciaStart and <= AciaEnd)
            {
                return (address & 1) == 0
                    ? ReadAciaStatus()
                    : ReadAciaData();
            }

            return serialUlaControl;
        }

        public void Write(ushort address, byte value)
        {
            if (address is >= AciaStart and <= AciaEnd)
            {
                return;
            }

            serialUlaControl = value;

            // The cassette motor is controlled via the serial ULA. The exact bit
            // layout also selects serial/tape routing and cassette baud rate; for
            // now we only preserve the observable stopped/running state.
            MotorRunning = (value & 0x80) != 0;
        }

        public void StopTape()
        {
            TapePlaying = false;
            MotorRunning = false;
        }

        private byte ReadAciaStatus()
        {
            byte status = AciaStatusTransmitDataEmpty | AciaStatusClearToSend;

            if (TapePlaying)
                status |= AciaStatusReceiveDataFull;
            else
                status |= AciaStatusDataCarrierDetect;

            return status;
        }

        private byte ReadAciaData()
        {
            return 0x00;
        }
    }
}
