using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BBC.Tests
{
    [TestClass]
    public sealed class SheilaAddressDecodeTests
    {
        [TestMethod]
        public void DevicePredicatesCoverTheirModelBChipSelectBlocks()
        {
            AssertBlock(HD6845_Video.IsSheilaAddress, 0xFE00, 0xFE07);
            AssertBlock(HD6845_Video.IsSheilaAddress, 0xFE20, 0xFE2F);
            AssertBlock(SerialACIA.IsAddress, 0xFE08, 0xFE1F);
            AssertBlock(System6522Via.IsAddress, 0xFE40, 0xFE5F);
            AssertBlock(User6522Via.IsAddress, 0xFE60, 0xFE7F);
            AssertBlock(Intel8271_Disk.IsAddress, 0xFE80, 0xFE9F);
            AssertBlock(uPD7002_ADC.IsAddress, 0xFEC0, 0xFEDF);
            AssertBlock(TubeUla.IsHostAddress, 0xFEE0, 0xFEFF);
        }

        [TestMethod]
        public void CrtcMirrorsUseA0ToSelectAddressOrData()
        {
            HD6845_Video video = new HD6845_Video(new byte[0x10000]);

            video.WriteSheila(0xFE06, 1);
            video.WriteSheila(0xFE07, 40);

            Assert.AreEqual((byte)1, video.ReadSheila(0xFE02));
            Assert.AreEqual((byte)40, video.ReadSheila(0xFE05));
        }

        [TestMethod]
        public void VideoUlaMirrorsUseA0ToSelectControlOrPalette()
        {
            HD6845_Video video = new HD6845_Video(new byte[0x10000]);

            video.WriteSheila(0xFE2E, 0x9C);
            video.WriteSheila(0xFE2F, 0x47);

            Assert.AreEqual((byte)0x9C, video.ReadSheila(0xFE24));
            Assert.AreEqual((byte)0x47, video.ReadSheila(0xFE2B));
        }

        [TestMethod]
        public void RomselCanBeWrittenAndReadThroughAnyAddressInItsBlock()
        {
            Emulator emulator = new Emulator();
            emulator.Initialise();

            emulator.Memory.WriteByte(0xFE3F, 6);

            Assert.AreEqual((byte)6, emulator.Memory.ReadByte(0xFE30));
            Assert.AreEqual((byte)6, emulator.Memory.ReadByte(0xFE37));
        }

        [TestMethod]
        public void MirroredDeviceRegistersSelectTheSameUnderlyingRegister()
        {
            System6522Via systemVia = new System6522Via(new SN76489_Sound());
            User6522Via userVia = new User6522Via();
            SerialACIA serial = new SerialACIA();
            uPD7002_ADC adc = new uPD7002_ADC();
            TubeUla tube = new TubeUla();

            systemVia.Write(0xFE52, 0xA5);
            userVia.Write(0xFE72, 0x5A);
            serial.Write(0xFE1F, 0x80);
            adc.Write(0xFEDC, 2);

            Assert.AreEqual((byte)0xA5, systemVia.Read(0xFE42));
            Assert.AreEqual((byte)0x5A, userVia.Read(0xFE62));
            Assert.IsTrue(serial.MotorRunning);
            Assert.AreEqual(2, adc.Read(0xFEC0) & 0x03);
            Assert.AreEqual(tube.ReadHost(0xFEE0), tube.ReadHost(0xFEF8));
        }

        [TestMethod]
        public void StepOutIgnoresSavedStackValuesAndNestedCalls()
        {
            int depth = 0;

            Assert.IsFalse(DebuggerWindow.TrackStepOutInstruction(ref depth, 0x48, 0xFD, 0xFC)); // PHA
            Assert.IsFalse(DebuggerWindow.TrackStepOutInstruction(ref depth, 0x20, 0xFC, 0xFA)); // JSR
            Assert.IsFalse(DebuggerWindow.TrackStepOutInstruction(ref depth, null, 0xFA, 0xFC)); // host-handled RTS
            Assert.AreEqual(0, depth);
            Assert.IsFalse(DebuggerWindow.TrackStepOutInstruction(ref depth, 0x40, 0xF9, 0xFC)); // RTI
            Assert.IsTrue(DebuggerWindow.TrackStepOutInstruction(ref depth, 0x60, 0xFC, 0xFE)); // RTS
        }

        private static void AssertBlock(Func<ushort, bool> predicate, ushort start, ushort end)
        {
            for (int address = start; address <= end; address++)
                Assert.IsTrue(predicate((ushort)address), $"Expected ${address:X4} to be decoded");
            if (start > 0xFE00) Assert.IsFalse(predicate((ushort)(start - 1)));
            if (end < 0xFEFF) Assert.IsFalse(predicate((ushort)(end + 1)));
        }
    }
}
