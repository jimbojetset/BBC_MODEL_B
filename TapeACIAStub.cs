// ============================================================================
// Project:     BBC
// File:        TapeACIAStub.cs
// Description: Minimal BBC cassette and serial ACIA state used by software
//              probing cassette/RS423 hardware registers.
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
    public sealed class TapeACIAStub
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

        /// <summary>Checks whether a SHEILA address belongs to the cassette ACIA range.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>True when address is true; otherwise, false.</returns>
        public static bool IsAddress(ushort address)
        {
            return address is >= AciaStart and <= AciaEnd
                || address is >= SerialUlaStart and <= SerialUlaEnd;
        }

        /// <summary>Reads  from emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <returns>The value read from emulated memory or device state.</returns>
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

        /// <summary>Writes  into emulated memory or device state.</summary>
        /// <param name="address">The CPU-visible address.</param>
        /// <param name="value">The input value.</param>
        public void Write(ushort address, byte value)
        {
            if (address is >= AciaStart and <= AciaEnd)
            {
                return;
            }

            serialUlaControl = value;

            MotorRunning = (value & 0x80) != 0;
        }

        /// <summary>Stops tape.</summary>
        public void StopTape()
        {
            TapePlaying = false;
            MotorRunning = false;
        }

        /// <summary>Builds the cassette ACIA status byte from motor and data-available state.</summary>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadAciaStatus()
        {
            byte status = AciaStatusTransmitDataEmpty | AciaStatusClearToSend;

            if (TapePlaying)
                status |= AciaStatusReceiveDataFull;
            else
                status |= AciaStatusDataCarrierDetect;

            return status;
        }

        /// <summary>Returns the current cassette ACIA receive-data byte.</summary>
        /// <returns>The value read from emulated memory or device state.</returns>
        private byte ReadAciaData()
        {
            return 0x00;
        }
    }
}
