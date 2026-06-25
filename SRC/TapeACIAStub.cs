// ============================================================================
// Project:     BBC
// File:        TapeACIAStub.cs
// Description: Minimal cassette/RS423 ACIA response for software that probes
//              the BBC's tape hardware even when loading from disc images.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See COPYING in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

namespace BBC
{

    /// <summary>
    /// Some disc conversions still touch the cassette ACIA. This keeps those
    /// probes harmless without pretending to implement tape loading.
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

        public bool MotorRunning { get; private set; }

        public bool TapePlaying { get; private set; }

        /// <summary>The cassette/RS423 ACIA occupies the FE08-FE0F part of SHEILA.</summary>
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
