// ============================================================================
// Project:     BBC
// File:        InputProfile.cs
// Description: Host input mapping to BBC keyboard matrix and joystick lines.
// Author:      James Booth
// Created:     2026
// License:     GPL-2.0-only - See LICENSE in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      BBC Micro ROMs are property of their respective rights holders.
//              This emulator is for educational purposes only.
// ============================================================================

using System.Text.Json;

namespace BBC
{

    public sealed class InputProfile
    {
        private readonly Dictionary<int, BbcKeyBinding> unmodifiedKeys = new Dictionary<int, BbcKeyBinding>();
        private readonly Dictionary<int, BbcKeyBinding> shiftedKeys = new Dictionary<int, BbcKeyBinding>();
        private readonly Dictionary<int, BbcKeyBinding> optionKeys = new Dictionary<int, BbcKeyBinding>();
        private readonly Dictionary<int, JoystickControl> keyboardJoystick = new Dictionary<int, JoystickControl>();
        private readonly Dictionary<byte, JoystickControl> controllerButtons = new Dictionary<byte, JoystickControl>();
        private readonly Dictionary<byte, JoystickAxis> controllerAxes = new Dictionary<byte, JoystickAxis>();
        private readonly Dictionary<byte, JoystickControl[]> controllerAxisDirections = new Dictionary<byte, JoystickControl[]>();
        private readonly Dictionary<byte, JoystickControl> joystickButtons = new Dictionary<byte, JoystickControl>();
        private readonly Dictionary<byte, JoystickAxis> joystickAxes = new Dictionary<byte, JoystickAxis>();
        private readonly Dictionary<byte, JoystickControl[]> joystickAxisDirections = new Dictionary<byte, JoystickControl[]>();
        private readonly Dictionary<int, BbcKeyBinding> customKeys = new Dictionary<int, BbcKeyBinding>();

        private InputProfile()
        {
        }

        public bool SyncHostCapsLock { get; private set; } = true;

        public string Name { get; private set; } = "Default";

        public static InputProfile CreateDefault()
        {
            InputProfile profile = new InputProfile();

            foreach (BbcPhysicalKey key in BbcKeyboard.Keys)
            {
                if (key.PrimaryHostKey.HasValue)
                    profile.MapKey(key.PrimaryHostKey.Value, key.InternalKey);

                foreach (int shiftedHostKey in key.ShiftedHostKeys)
                    profile.MapKey(shiftedHostKey, key.InternalKey, BbcShiftAdjustment.Force);

                foreach (BbcHostShiftAlias alias in key.HostShiftAliases)
                    profile.MapShiftedKey(alias.HostKey, key.InternalKey, alias.Adjustment);
            }

            profile.MapKey(SdlKey.LShift, BbcKeyboard.LeftShiftKey);
            profile.MapKey(SdlKey.RShift, BbcKeyboard.RightShiftKey);
            profile.MapKey(SdlKey.LCtrl, 0x01);
            profile.MapKey(SdlKey.RCtrl, 0x01);
            profile.MapKey(SdlKey.Backspace, 0x59);
            profile.MapKey(SdlKey.Delete, 0x59);
            profile.MapKey(SdlKey.Insert, 0x69);
            profile.MapKey(SdlKey.Section, 0x69);
            profile.MapOptionKey(SdlKey.Num3, 0x28, BbcShiftAdjustment.Force);

            profile.MapKeyboardJoystick(SdlKey.Left, JoystickControl.Left);
            profile.MapKeyboardJoystick(SdlKey.Right, JoystickControl.Right);
            profile.MapKeyboardJoystick(SdlKey.Up, JoystickControl.Up);
            profile.MapKeyboardJoystick(SdlKey.Down, JoystickControl.Down);
            profile.MapKeyboardJoystick(SdlKey.Space, JoystickControl.Fire);

            profile.MapControllerAxis(SdlControllerAxis.LeftX, JoystickAxis.X, JoystickControl.Left, JoystickControl.Right);
            profile.MapControllerAxis(SdlControllerAxis.LeftY, JoystickAxis.Y, JoystickControl.Up, JoystickControl.Down);
            profile.MapControllerButton(SdlControllerButton.A, JoystickControl.Fire);
            profile.MapControllerButton(SdlControllerButton.DpadUp, JoystickControl.Up);
            profile.MapControllerButton(SdlControllerButton.DpadDown, JoystickControl.Down);
            profile.MapControllerButton(SdlControllerButton.DpadLeft, JoystickControl.Left);
            profile.MapControllerButton(SdlControllerButton.DpadRight, JoystickControl.Right);

            profile.MapJoystickAxis(0, JoystickAxis.X, JoystickControl.Left, JoystickControl.Right);
            profile.MapJoystickAxis(1, JoystickAxis.Y, JoystickControl.Up, JoystickControl.Down);
            profile.MapJoystickButton(0, JoystickControl.Fire);

            return profile;
        }

        public static InputProfile CreateEmulatorDefault()
        {
            string path = Path.Combine(Environment.CurrentDirectory, "Assets", "DefaultInputProfile.json");
            return File.Exists(path) ? Load(path) : CreateDefault();
        }

        public static InputProfile Load(string path)
        {
            return Load(path, Path.GetFileNameWithoutExtension(path));
        }

        private static InputProfile Load(string path, string profileName)
        {
            InputProfile profile = CreateDefault();
            profile.Name = profileName;

            if (!File.Exists(path))
                return profile;

            try
            {
                string json = File.ReadAllText(path);
                InputProfileFile? file = JsonSerializer.Deserialize<InputProfileFile>(json, JsonOptions);
                if (file is null)
                    return profile;

                profile.SyncHostCapsLock = file.SyncHostCapsLock;
                foreach (InputKeyFile key in file.Keys)
                {
                    BbcShiftAdjustment shift = Enum.TryParse(key.Shift, ignoreCase: true, out BbcShiftAdjustment parsed)
                        ? parsed
                        : BbcShiftAdjustment.Preserve;
                    profile.BindHostKey(key.HostKey, key.BbcKey, shift);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.WriteLine($"Input profile '{path}' ignored: {ex.Message}");
            }

            return profile;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            Name = Path.GetFileNameWithoutExtension(path);

            InputProfileFile file = new InputProfileFile
            {
                Name = Name,
                SyncHostCapsLock = SyncHostCapsLock
            };

            foreach (KeyValuePair<int, BbcKeyBinding> pair in customKeys.OrderBy(pair => pair.Key))
            {
                file.Keys.Add(new InputKeyFile
                {
                    HostKey = pair.Key,
                    BbcKey = pair.Value.InternalKey,
                    Shift = pair.Value.ShiftAdjustment.ToString()
                });
            }

            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        }

        public void BindHostKey(int keySym, byte internalKey, BbcShiftAdjustment shiftAdjustment = BbcShiftAdjustment.Preserve)
        {
            RemoveBbcKeyBinding(internalKey);
            unmodifiedKeys[keySym] = new BbcKeyBinding(internalKey, shiftAdjustment);
            customKeys[keySym] = new BbcKeyBinding(internalKey, shiftAdjustment);
            keyboardJoystick.Remove(keySym);
            if (keySym == SdlKey.CapsLock)
                SyncHostCapsLock = false;
        }

        public void ResetToDefault()
        {
            string name = Name;
            InputProfile defaults = CreateEmulatorDefault();
            unmodifiedKeys.Clear();
            shiftedKeys.Clear();
            optionKeys.Clear();
            keyboardJoystick.Clear();
            controllerButtons.Clear();
            controllerAxes.Clear();
            controllerAxisDirections.Clear();
            joystickButtons.Clear();
            joystickAxes.Clear();
            joystickAxisDirections.Clear();

            CopyFrom(defaults);
            customKeys.Clear();
            Name = name;
        }

        public string GetPrimaryHostKeyName(byte internalKey, BbcShiftAdjustment shiftAdjustment = BbcShiftAdjustment.Preserve)
        {
            foreach (KeyValuePair<int, BbcKeyBinding> pair in OrderPrimaryBindingsForDisplay(customKeys, shiftAdjustment))
            {
                if (pair.Value.InternalKey == internalKey)
                    return SdlKey.GetName(pair.Key);
            }

            foreach (KeyValuePair<int, BbcKeyBinding> pair in OrderPrimaryBindingsForDisplay(unmodifiedKeys, shiftAdjustment))
            {
                if (pair.Value.InternalKey == internalKey)
                    return SdlKey.GetName(pair.Key);
            }

            return string.Empty;
        }

        private static IOrderedEnumerable<KeyValuePair<int, BbcKeyBinding>> OrderPrimaryBindingsForDisplay(
            Dictionary<int, BbcKeyBinding> bindings,
            BbcShiftAdjustment shiftAdjustment)
        {
            return bindings
                .Where(pair => pair.Value.ShiftAdjustment == shiftAdjustment)
                .OrderBy(pair => pair.Key);
        }

        public BbcKeyBinding? MapHostKey(int keySym, int modifiers)
        {
            if ((modifiers & SdlModifier.Alt) != 0 && optionKeys.TryGetValue(keySym, out BbcKeyBinding optionKey))
                return optionKey;

            if ((modifiers & SdlModifier.Shift) != 0 && shiftedKeys.TryGetValue(keySym, out BbcKeyBinding shiftedKey))
                return shiftedKey;

            return unmodifiedKeys.TryGetValue(keySym, out BbcKeyBinding key)
                ? key
                : null;
        }

        public JoystickControl? MapKeyboardJoystick(int keySym)
        {
            return keyboardJoystick.TryGetValue(keySym, out JoystickControl control)
                ? control
                : null;
        }

        public bool TryMapControllerAxis(byte axis, out JoystickAxis joystickAxis, out JoystickControl negative, out JoystickControl positive)
        {
            if (controllerAxes.TryGetValue(axis, out joystickAxis)
                && controllerAxisDirections.TryGetValue(axis, out JoystickControl[]? directions))
            {
                negative = directions[0];
                positive = directions[1];
                return true;
            }

            negative = default;
            positive = default;
            return false;
        }

        public JoystickControl? MapControllerButton(byte button)
        {
            return controllerButtons.TryGetValue(button, out JoystickControl control)
                ? control
                : null;
        }

        public bool TryMapJoystickAxis(byte axis, out JoystickAxis joystickAxis, out JoystickControl negative, out JoystickControl positive)
        {
            if (joystickAxes.TryGetValue(axis, out joystickAxis)
                && joystickAxisDirections.TryGetValue(axis, out JoystickControl[]? directions))
            {
                negative = directions[0];
                positive = directions[1];
                return true;
            }

            negative = default;
            positive = default;
            return false;
        }

        public JoystickControl? MapJoystickButton(byte button)
        {
            return joystickButtons.TryGetValue(button, out JoystickControl control)
                ? control
                : null;
        }

        private void MapKey(int keySym, byte internalKey, BbcShiftAdjustment shiftAdjustment = BbcShiftAdjustment.Preserve)
        {
            unmodifiedKeys[keySym] = new BbcKeyBinding(internalKey, shiftAdjustment);
        }

        private void MapShiftedKey(int keySym, byte internalKey, BbcShiftAdjustment shiftAdjustment = BbcShiftAdjustment.Preserve)
        {
            shiftedKeys[keySym] = new BbcKeyBinding(internalKey, shiftAdjustment);
        }

        private void MapOptionKey(int keySym, byte internalKey, BbcShiftAdjustment shiftAdjustment = BbcShiftAdjustment.Preserve)
        {
            optionKeys[keySym] = new BbcKeyBinding(internalKey, shiftAdjustment);
        }

        private void MapKeyboardJoystick(int keySym, JoystickControl control)
        {
            keyboardJoystick[keySym] = control;
        }

        private void MapControllerAxis(byte axis, JoystickAxis joystickAxis, JoystickControl negative, JoystickControl positive)
        {
            controllerAxes[axis] = joystickAxis;
            controllerAxisDirections[axis] = [negative, positive];
        }

        private void MapControllerButton(byte button, JoystickControl control)
        {
            controllerButtons[button] = control;
        }

        private void MapJoystickAxis(byte axis, JoystickAxis joystickAxis, JoystickControl negative, JoystickControl positive)
        {
            joystickAxes[axis] = joystickAxis;
            joystickAxisDirections[axis] = [negative, positive];
        }

        private void MapJoystickButton(byte button, JoystickControl control)
        {
            joystickButtons[button] = control;
        }

        private void RemoveBbcKeyBinding(byte internalKey)
        {
            foreach (int key in unmodifiedKeys
                .Where(pair => pair.Value.InternalKey == internalKey)
                .Select(pair => pair.Key)
                .ToArray())
            {
                unmodifiedKeys.Remove(key);
                customKeys.Remove(key);
            }
        }

        private void CopyFrom(InputProfile source)
        {
            foreach (KeyValuePair<int, BbcKeyBinding> pair in source.unmodifiedKeys)
                unmodifiedKeys[pair.Key] = pair.Value;
            foreach (KeyValuePair<int, BbcKeyBinding> pair in source.shiftedKeys)
                shiftedKeys[pair.Key] = pair.Value;
            foreach (KeyValuePair<int, BbcKeyBinding> pair in source.optionKeys)
                optionKeys[pair.Key] = pair.Value;
            foreach (KeyValuePair<int, JoystickControl> pair in source.keyboardJoystick)
                keyboardJoystick[pair.Key] = pair.Value;
            foreach (KeyValuePair<byte, JoystickControl> pair in source.controllerButtons)
                controllerButtons[pair.Key] = pair.Value;
            foreach (KeyValuePair<byte, JoystickAxis> pair in source.controllerAxes)
                controllerAxes[pair.Key] = pair.Value;
            foreach (KeyValuePair<byte, JoystickControl[]> pair in source.controllerAxisDirections)
                controllerAxisDirections[pair.Key] = [pair.Value[0], pair.Value[1]];
            foreach (KeyValuePair<byte, JoystickControl> pair in source.joystickButtons)
                joystickButtons[pair.Key] = pair.Value;
            foreach (KeyValuePair<byte, JoystickAxis> pair in source.joystickAxes)
                joystickAxes[pair.Key] = pair.Value;
            foreach (KeyValuePair<byte, JoystickControl[]> pair in source.joystickAxisDirections)
                joystickAxisDirections[pair.Key] = [pair.Value[0], pair.Value[1]];
            SyncHostCapsLock = source.SyncHostCapsLock;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private sealed class InputProfileFile
        {
            public string Name { get; set; } = "Default";

            public bool SyncHostCapsLock { get; set; } = true;

            public List<InputKeyFile> Keys { get; set; } = new List<InputKeyFile>();
        }

        private sealed class InputKeyFile
        {
            public int HostKey { get; set; }

            public byte BbcKey { get; set; }

            public string Shift { get; set; } = nameof(BbcShiftAdjustment.Preserve);
        }
    }

    public readonly record struct BbcKeyBinding(byte InternalKey, BbcShiftAdjustment ShiftAdjustment)
    {
        public byte MatrixKey => BbcKeyboard.GetMatrixKey(InternalKey);
    }

    public enum BbcShiftAdjustment
    {
        Preserve,
        Suppress,
        Force
    }

    public readonly record struct BbcPhysicalKey(
        byte InternalKey,
        string Label,
        int? PrimaryHostKey,
        int[] ShiftedHostKeys,
        BbcHostShiftAlias[] HostShiftAliases);

    public readonly record struct BbcHostShiftAlias(int HostKey, BbcShiftAdjustment Adjustment);

    public static class BbcKeyboard
    {
        public const byte LeftShiftKey = 0x00;
        public const byte RightShiftKey = 0x80;

        public static readonly BbcPhysicalKey[] Keys =
        [
            Key(0x10, "Q", SdlKey.Q),
            Key(0x11, "3", SdlKey.Num3, [SdlKey.Hash]),
            Key(0x12, "4", SdlKey.Num4, [SdlKey.Dollar]),
            Key(0x13, "5", SdlKey.Num5, [SdlKey.Percent]),
            Key(0x14, "F4", SdlKey.F4),
            Key(0x15, "8", SdlKey.Num8, [SdlKey.LeftParen]),
            Key(0x16, "F7", SdlKey.F7),
            Key(0x17, "-", SdlKey.Minus, [SdlKey.EqualsKey]),
            Key(0x18, "^", SdlKey.Caret, [SdlKey.Tilde]),
            Key(0x19, "LEFT", SdlKey.Left),
            Key(0x20, "F0", SdlKey.F10),
            Key(0x21, "W", SdlKey.W),
            Key(0x22, "E", SdlKey.E),
            Key(0x23, "T", SdlKey.T),
            Key(0x24, "7", SdlKey.Num7, [SdlKey.Apostrophe]),
            Key(0x25, "I", SdlKey.I),
            Key(0x26, "9", SdlKey.Num9, [SdlKey.RightParen]),
            Key(0x27, "0", SdlKey.Num0),
            Key(0x28, "_", SdlKey.Underscore, [SdlKey.Pound], [Shift(SdlKey.Minus, BbcShiftAdjustment.Suppress)]),
            Key(0x29, "DOWN", SdlKey.Down),
            Key(0x30, "1", SdlKey.Num1, [SdlKey.Exclamation]),
            Key(0x31, "2", SdlKey.Num2, [SdlKey.DoubleQuote]),
            Key(0x32, "D", SdlKey.D),
            Key(0x33, "R", SdlKey.R),
            Key(0x34, "6", SdlKey.Num6, [SdlKey.Ampersand]),
            Key(0x35, "U", SdlKey.U),
            Key(0x36, "O", SdlKey.O),
            Key(0x37, "P", SdlKey.P),
            Key(0x38, "[", SdlKey.LeftBracket, [SdlKey.LeftBrace]),
            Key(0x39, "UP", SdlKey.Up),
            Key(0x40, "CAPS", null),
            Key(0x41, "A", SdlKey.A),
            Key(0x42, "X", SdlKey.X),
            Key(0x43, "F", SdlKey.F),
            Key(0x44, "Y", SdlKey.Y),
            Key(0x45, "J", SdlKey.J),
            Key(0x46, "K", SdlKey.K),
            Key(0x47, "@", SdlKey.At, [SdlKey.BackQuote], [Shift(SdlKey.Num2, BbcShiftAdjustment.Suppress)]),
            Key(0x48, ":", SdlKey.Colon, [SdlKey.Asterisk], [Shift(SdlKey.Semicolon, BbcShiftAdjustment.Suppress)]),
            Key(0x49, "RETURN", SdlKey.Return),
            Key(0x51, "S", SdlKey.S),
            Key(0x52, "C", SdlKey.C),
            Key(0x53, "G", SdlKey.G),
            Key(0x54, "H", SdlKey.H),
            Key(0x55, "N", SdlKey.N),
            Key(0x56, "L", SdlKey.L),
            Key(0x57, ";", SdlKey.Semicolon, [SdlKey.Plus]),
            Key(0x58, "]", SdlKey.RightBracket, [SdlKey.RightBrace]),
            Key(0x59, "DEL", null),
            Key(0x60, "TAB", SdlKey.Tab),
            Key(0x61, "Z", SdlKey.Z),
            Key(0x62, "SPACE", SdlKey.Space),
            Key(0x63, "V", SdlKey.V),
            Key(0x64, "B", SdlKey.B),
            Key(0x65, "M", SdlKey.M),
            Key(0x66, ",", SdlKey.Comma, [SdlKey.LessThan]),
            Key(0x67, ".", SdlKey.Period, [SdlKey.GreaterThan]),
            Key(0x68, "/", SdlKey.Slash, [SdlKey.Question]),
            Key(0x69, "COPY", null),
            Key(0x70, "ESC", SdlKey.Escape),
            Key(0x71, "F1", SdlKey.F1),
            Key(0x72, "F2", SdlKey.F2),
            Key(0x73, "F3", SdlKey.F3),
            Key(0x74, "F5", SdlKey.F5),
            Key(0x75, "F6", SdlKey.F6),
            Key(0x76, "F8", SdlKey.F8),
            Key(0x77, "F9", SdlKey.F9),
            Key(0x78, "\\", SdlKey.Backslash, [SdlKey.Pipe]),
            Key(0x79, "RIGHT", SdlKey.Right)
        ];

        private static BbcPhysicalKey Key(
            byte internalKey,
            string label,
            int? primaryHostKey,
            int[]? shiftedHostKeys = null,
            BbcHostShiftAlias[]? hostShiftAliases = null)
        {
            return new BbcPhysicalKey(
                internalKey,
                label,
                primaryHostKey,
                shiftedHostKeys ?? [],
                hostShiftAliases ?? []);
        }

        private static BbcHostShiftAlias Shift(
            int hostKey,
            BbcShiftAdjustment adjustment = BbcShiftAdjustment.Preserve)
        {
            return new BbcHostShiftAlias(hostKey, adjustment);
        }

        public static byte GetMatrixKey(byte internalKey)
        {
            return internalKey == RightShiftKey ? LeftShiftKey : internalKey;
        }

        public static bool TryMapCharacter(char ch, out BbcKeyBinding key)
        {
            if (ch >= 'a' && ch <= 'z')
                return TryMapLetter(ch, BbcShiftAdjustment.Suppress, out key);

            if (ch >= 'A' && ch <= 'Z')
                return TryMapLetter(char.ToLowerInvariant(ch), BbcShiftAdjustment.Force, out key);

            key = ch switch
            {
                ' ' => new BbcKeyBinding(0x62, BbcShiftAdjustment.Suppress),
                '0' => new BbcKeyBinding(0x27, BbcShiftAdjustment.Suppress),
                '1' => new BbcKeyBinding(0x30, BbcShiftAdjustment.Suppress),
                '2' => new BbcKeyBinding(0x31, BbcShiftAdjustment.Suppress),
                '3' => new BbcKeyBinding(0x11, BbcShiftAdjustment.Suppress),
                '4' => new BbcKeyBinding(0x12, BbcShiftAdjustment.Suppress),
                '5' => new BbcKeyBinding(0x13, BbcShiftAdjustment.Suppress),
                '6' => new BbcKeyBinding(0x34, BbcShiftAdjustment.Suppress),
                '7' => new BbcKeyBinding(0x24, BbcShiftAdjustment.Suppress),
                '8' => new BbcKeyBinding(0x15, BbcShiftAdjustment.Suppress),
                '9' => new BbcKeyBinding(0x26, BbcShiftAdjustment.Suppress),
                '!' => new BbcKeyBinding(0x30, BbcShiftAdjustment.Force),
                '"' => new BbcKeyBinding(0x31, BbcShiftAdjustment.Force),
                '#' => new BbcKeyBinding(0x11, BbcShiftAdjustment.Force),
                '$' => new BbcKeyBinding(0x12, BbcShiftAdjustment.Force),
                '%' => new BbcKeyBinding(0x13, BbcShiftAdjustment.Force),
                '&' => new BbcKeyBinding(0x34, BbcShiftAdjustment.Force),
                '\'' => new BbcKeyBinding(0x24, BbcShiftAdjustment.Force),
                '(' => new BbcKeyBinding(0x15, BbcShiftAdjustment.Force),
                ')' => new BbcKeyBinding(0x26, BbcShiftAdjustment.Force),
                '-' => new BbcKeyBinding(0x17, BbcShiftAdjustment.Suppress),
                '=' => new BbcKeyBinding(0x17, BbcShiftAdjustment.Force),
                '^' => new BbcKeyBinding(0x18, BbcShiftAdjustment.Suppress),
                '~' => new BbcKeyBinding(0x18, BbcShiftAdjustment.Force),
                '_' => new BbcKeyBinding(0x28, BbcShiftAdjustment.Suppress),
                '£' => new BbcKeyBinding(0x28, BbcShiftAdjustment.Force),
                '@' => new BbcKeyBinding(0x47, BbcShiftAdjustment.Suppress),
                '`' => new BbcKeyBinding(0x47, BbcShiftAdjustment.Force),
                ':' => new BbcKeyBinding(0x48, BbcShiftAdjustment.Suppress),
                '*' => new BbcKeyBinding(0x48, BbcShiftAdjustment.Force),
                ';' => new BbcKeyBinding(0x57, BbcShiftAdjustment.Suppress),
                '+' => new BbcKeyBinding(0x57, BbcShiftAdjustment.Force),
                '[' => new BbcKeyBinding(0x38, BbcShiftAdjustment.Suppress),
                '{' => new BbcKeyBinding(0x38, BbcShiftAdjustment.Force),
                ']' => new BbcKeyBinding(0x58, BbcShiftAdjustment.Suppress),
                '}' => new BbcKeyBinding(0x58, BbcShiftAdjustment.Force),
                ',' => new BbcKeyBinding(0x66, BbcShiftAdjustment.Suppress),
                '<' => new BbcKeyBinding(0x66, BbcShiftAdjustment.Force),
                '.' => new BbcKeyBinding(0x67, BbcShiftAdjustment.Suppress),
                '>' => new BbcKeyBinding(0x67, BbcShiftAdjustment.Force),
                '/' => new BbcKeyBinding(0x68, BbcShiftAdjustment.Suppress),
                '?' => new BbcKeyBinding(0x68, BbcShiftAdjustment.Force),
                '\\' => new BbcKeyBinding(0x78, BbcShiftAdjustment.Suppress),
                '|' => new BbcKeyBinding(0x78, BbcShiftAdjustment.Force),
                '\r' or '\n' => new BbcKeyBinding(0x49, BbcShiftAdjustment.Preserve),
                '\t' => new BbcKeyBinding(0x60, BbcShiftAdjustment.Preserve),
                _ => default
            };

            return ch is >= ' ' and <= '~' or '£' or '\r' or '\n' or '\t'
                && key.InternalKey != 0;
        }

        private static bool TryMapLetter(char ch, BbcShiftAdjustment shiftAdjustment, out BbcKeyBinding key)
        {
            key = ch switch
            {
                'a' => new BbcKeyBinding(0x41, shiftAdjustment),
                'b' => new BbcKeyBinding(0x64, shiftAdjustment),
                'c' => new BbcKeyBinding(0x52, shiftAdjustment),
                'd' => new BbcKeyBinding(0x32, shiftAdjustment),
                'e' => new BbcKeyBinding(0x22, shiftAdjustment),
                'f' => new BbcKeyBinding(0x43, shiftAdjustment),
                'g' => new BbcKeyBinding(0x53, shiftAdjustment),
                'h' => new BbcKeyBinding(0x54, shiftAdjustment),
                'i' => new BbcKeyBinding(0x25, shiftAdjustment),
                'j' => new BbcKeyBinding(0x45, shiftAdjustment),
                'k' => new BbcKeyBinding(0x46, shiftAdjustment),
                'l' => new BbcKeyBinding(0x56, shiftAdjustment),
                'm' => new BbcKeyBinding(0x65, shiftAdjustment),
                'n' => new BbcKeyBinding(0x55, shiftAdjustment),
                'o' => new BbcKeyBinding(0x36, shiftAdjustment),
                'p' => new BbcKeyBinding(0x37, shiftAdjustment),
                'q' => new BbcKeyBinding(0x10, shiftAdjustment),
                'r' => new BbcKeyBinding(0x33, shiftAdjustment),
                's' => new BbcKeyBinding(0x51, shiftAdjustment),
                't' => new BbcKeyBinding(0x23, shiftAdjustment),
                'u' => new BbcKeyBinding(0x35, shiftAdjustment),
                'v' => new BbcKeyBinding(0x63, shiftAdjustment),
                'w' => new BbcKeyBinding(0x21, shiftAdjustment),
                'x' => new BbcKeyBinding(0x42, shiftAdjustment),
                'y' => new BbcKeyBinding(0x44, shiftAdjustment),
                'z' => new BbcKeyBinding(0x61, shiftAdjustment),
                _ => default
            };

            return key.InternalKey != 0;
        }
    }

    internal static class SdlModifier
    {
        public const int Shift = 0x0003;
        public const int Ctrl = 0x00C0;
        public const int LeftShift = 0x0001;
        public const int LeftCtrl = 0x0040;
        public const int Alt = 0x0300;
        public const int Gui = 0x0C00;
        public const int Caps = 0x2000;
    }

    internal static class SdlKey
    {
        public const int Space = 32;
        public const int Exclamation = 33;
        public const int Asterisk = 42;
        public const int Plus = 43;
        public const int At = 64;
        public const int Caret = 94;
        public const int Hash = 35;
        public const int Apostrophe = 39;
        public const int DoubleQuote = 34;
        public const int Dollar = 36;
        public const int Percent = 37;
        public const int Ampersand = 38;
        public const int LeftParen = 40;
        public const int RightParen = 41;
        public const int LessThan = 60;
        public const int GreaterThan = 62;
        public const int Question = 63;
        public const int BackQuote = 96;
        public const int LeftBrace = 123;
        public const int Pipe = 124;
        public const int RightBrace = 125;
        public const int Tilde = 126;
        public const int Section = 167;
        public const int Pound = 163;
        public const int Underscore = 95;
        public const int Num0 = 48;
        public const int Num1 = 49;
        public const int Num2 = 50;
        public const int Num3 = 51;
        public const int Num4 = 52;
        public const int Num5 = 53;
        public const int Num6 = 54;
        public const int Num7 = 55;
        public const int Num8 = 56;
        public const int Num9 = 57;
        public const int Colon = 58;
        public const int Semicolon = 59;
        public const int Backspace = 8;
        public const int Tab = 9;
        public const int Return = 13;
        public const int Escape = 27;
        public const int Comma = 44;
        public const int Minus = 45;
        public const int Period = 46;
        public const int Slash = 47;
        public const int EqualsKey = 61;
        public const int Delete = 127;
        public const int LeftBracket = 91;
        public const int Backslash = 92;
        public const int RightBracket = 93;
        public const int A = 97;
        public const int B = 98;
        public const int C = 99;
        public const int D = 100;
        public const int E = 101;
        public const int F = 102;
        public const int G = 103;
        public const int H = 104;
        public const int I = 105;
        public const int J = 106;
        public const int K = 107;
        public const int L = 108;
        public const int M = 109;
        public const int N = 110;
        public const int O = 111;
        public const int P = 112;
        public const int Q = 113;
        public const int R = 114;
        public const int S = 115;
        public const int T = 116;
        public const int U = 117;
        public const int V = 118;
        public const int W = 119;
        public const int X = 120;
        public const int Y = 121;
        public const int Z = 122;
        public const int Right = 1073741903;
        public const int Left = 1073741904;
        public const int Down = 1073741905;
        public const int Up = 1073741906;
        public const int CapsLock = 1073741881;
        public const int F1 = 1073741882;
        public const int F2 = 1073741883;
        public const int F3 = 1073741884;
        public const int F4 = 1073741885;
        public const int F5 = 1073741886;
        public const int F6 = 1073741887;
        public const int F7 = 1073741888;
        public const int F8 = 1073741889;
        public const int F9 = 1073741890;
        public const int F10 = 1073741891;
        public const int F11 = 1073741892;
        public const int Insert = 1073741897;
        public const int KeypadMultiply = 1073741909;
        public const int KeypadEnter = 1073741912;
        public const int Return2 = 1073741982;
        public const int LCtrl = 1073742048;
        public const int LShift = 1073742049;
        public const int RCtrl = 1073742052;
        public const int RShift = 1073742053;
        public const int F12 = 1073741893;

        public static string GetName(int keySym)
        {
            return keySym switch
            {
                Space => "Space",
                Exclamation => "!",
                Asterisk => "*",
                Plus => "+",
                At => "@",
                Caret => "^",
                Hash => "#",
                Apostrophe => "'",
                DoubleQuote => "\"",
                Dollar => "$",
                Percent => "%",
                Ampersand => "&",
                LeftParen => "(",
                RightParen => ")",
                LessThan => "<",
                GreaterThan => ">",
                Question => "?",
                BackQuote => "`",
                LeftBrace => "{",
                Pipe => "|",
                RightBrace => "}",
                Tilde => "~",
                Section => "Section",
                Pound => "£",
                Underscore => "_",
                Num0 => "0",
                Num1 => "1",
                Num2 => "2",
                Num3 => "3",
                Num4 => "4",
                Num5 => "5",
                Num6 => "6",
                Num7 => "7",
                Num8 => "8",
                Num9 => "9",
                Colon => ":",
                Semicolon => ";",
                Backspace => "Backspace",
                Tab => "Tab",
                Return => "Return",
                Escape => "Escape",
                Comma => ",",
                Minus => "-",
                Period => ".",
                Slash => "/",
                EqualsKey => "=",
                Delete => "Delete",
                LeftBracket => "[",
                Backslash => "\\",
                RightBracket => "]",
                >= A and <= Z => ((char)keySym).ToString().ToUpperInvariant(),
                Right => "Right",
                Left => "Left",
                Down => "Down",
                Up => "Up",
                CapsLock => "Caps Lock",
                F1 => "F1",
                F2 => "F2",
                F3 => "F3",
                F4 => "F4",
                F5 => "F5",
                F6 => "F6",
                F7 => "F7",
                F8 => "F8",
                F9 => "F9",
                F10 => "F10",
                F11 => "F11",
                F12 => "F12",
                Insert => "Insert",
                KeypadMultiply => "Keypad *",
                KeypadEnter => "Keypad Enter",
                Return2 => "Return",
                LCtrl => "Left Ctrl",
                LShift => "Left Shift",
                RCtrl => "Right Ctrl",
                RShift => "Right Shift",
                _ => $"Key {keySym}"
            };
        }
    }

    internal static class SdlControllerAxis
    {
        public const byte LeftX = 0;
        public const byte LeftY = 1;
    }

    internal static class SdlControllerButton
    {
        public const byte A = 0;
        public const byte DpadUp = 11;
        public const byte DpadDown = 12;
        public const byte DpadLeft = 13;
        public const byte DpadRight = 14;
    }
}
