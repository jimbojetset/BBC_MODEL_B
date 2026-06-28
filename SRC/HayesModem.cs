// ============================================================================
// Project:     BBC
// File:        HayesModem.cs
// Description: Functional Hayes-compatible modem attached to the BBC RS423 ACIA.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Text;

namespace BBC
{

    public sealed class HayesModem
    {
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("BBC_SERIAL_TRACE") == "1";

        private readonly SerialACIA serialAcia;
        private readonly StringBuilder commandLine = new StringBuilder();
        private bool commandEcho = true;

        public HayesModem(SerialACIA serialAcia)
        {
            this.serialAcia = serialAcia;
        }

        public bool CommandEchoEnabled => commandEcho;

        public void Receive(byte value)
        {
            if (value == 0x00)
                return;

            if (commandEcho)
                serialAcia.QueueReceivedByte(value);

            if (value is 0x0D or 0x0A)
            {
                if (commandLine.Length > 0)
                    HandleCommandLine(commandLine.ToString());

                commandLine.Clear();
                return;
            }

            if (value == 0x08 || value == 0x7F)
            {
                if (commandLine.Length > 0)
                    commandLine.Length--;
                return;
            }

            if (value is >= 0x20 and <= 0x7E && commandLine.Length < 240)
                commandLine.Append((char)value);
        }

        private void HandleCommandLine(string line)
        {
            string command = line.Trim();
            if (command.Length == 0)
                return;

            Trace($"> {command}");

            if (!command.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            {
                Respond("ERROR");
                return;
            }

            string body = command.Length == 2 ? string.Empty : command[2..].Trim();
            if (body.Length == 0)
            {
                Respond("OK");
                return;
            }

            HandleCommandBody(body);
        }

        private void HandleCommandBody(string body)
        {
            string upper = body.ToUpperInvariant();

            if (upper is "Z" or "H" or "H0")
            {
                if (upper == "Z")
                    commandEcho = true;
                Respond("OK");
                return;
            }

            if (upper is "E0" or "E")
            {
                commandEcho = false;
                Respond("OK");
                return;
            }

            if (upper == "E1")
            {
                commandEcho = true;
                Respond("OK");
                return;
            }

            if (upper == "I")
            {
                Respond("BBC MODEL B HAYES MODEM");
                Respond("OK");
                return;
            }

            if (upper.StartsWith("D", StringComparison.Ordinal))
            {
                Respond("NO CARRIER");
                return;
            }

            Respond("ERROR");
        }

        private void Respond(string text)
        {
            serialAcia.QueueReceivedText($"\r\n{text}\r\n");
            Trace($"< {text}");
        }

        private static void Trace(string message)
        {
            if (TraceEnabled)
                Console.WriteLine($"[hayes] {message}");
        }
    }
}
