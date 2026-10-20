using UnityEngine;

namespace Lichronicle.Gameplay.Inputs
{
    public enum GamepadLayout { Auto, PlayStation, Xbox }

    /// <summary>Legacy Input Manager: native PS HID uses square/cross/circle/triangle = 0/1/2/3.</summary>
    public static class GamepadMapping
    {
        private static string[] names = new string[0];
        private static float nextScan;
        private static int activeIndex = -1, checkedFrame = -1;
        private static string detectedName;
        private static GamepadLayout detectedLayout;
        private static readonly string[] PlayStationLabels = { "□ 方塊", "× 叉", "○ 圓", "△ 三角", "L1", "R1", "L2", "R2", "Create", "Options", "L3", "R3", "PS", "觸控板" };
        private static readonly string[] XboxLabels = { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "LS", "RS" };
        private static readonly string[] PlayStationButtonLabels = BuildLabels(PlayStationLabels);
        private static readonly string[] XboxButtonLabels = BuildLabels(XboxLabels);
        public static string DeviceName { get { Refresh(); return activeIndex >= 0 ? names[activeIndex] : "未連接"; } }
        public static GamepadLayout Resolve(GamepadLayout preference)
        {
            Refresh();
            if (preference != GamepadLayout.Auto) return preference;
            string currentName = DeviceName;
            if (currentName == detectedName) return detectedLayout;
            detectedName = currentName;
            string name = currentName.ToLowerInvariant();
            detectedLayout = name.Contains("dualsense") || name.Contains("dualshock") || name.Contains("playstation")
                || name.Contains("wireless controller") || name.Contains("ps5") || name.Contains("ps4")
                ? GamepadLayout.PlayStation : GamepadLayout.Xbox;
            return detectedLayout;
        }
        private static void Refresh()
        {
            if (checkedFrame == Time.frameCount) return;
            checkedFrame = Time.frameCount;
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.5f;
                names = Input.GetJoystickNames();
                if (activeIndex >= names.Length || activeIndex < 0 || string.IsNullOrEmpty(names[activeIndex]))
                {
                    activeIndex = -1;
                    for (int i = 0; i < Mathf.Min(8, names.Length); i++) if (!string.IsNullOrEmpty(names[i])) { activeIndex = i; break; }
                }
            }
            // Last device to press a face button becomes the active mapping.
            for (int i = 0; i < Mathf.Min(8, names.Length); i++)
                if (!string.IsNullOrEmpty(names[i])) for (int button = 0; button < 4; button++)
                    if (Input.GetKeyDown(DeviceKey(i, button))) activeIndex = i;
        }
        private static KeyCode DeviceKey(int index, int button) => (KeyCode)((int)KeyCode.Joystick1Button0 + index * 20 + button);
        public static KeyCode JumpKey(GamepadLayout preference) => ButtonKey(Resolve(preference) == GamepadLayout.PlayStation ? 1 : 0);
        public static KeyCode DodgeKey(GamepadLayout preference) => ButtonKey(Resolve(preference) == GamepadLayout.PlayStation ? 2 : 1);
        public static KeyCode AttackKey(GamepadLayout preference, bool north) => ButtonKey(north ? 3 : Resolve(preference) == GamepadLayout.PlayStation ? 0 : 2);
        private static KeyCode ButtonKey(int button)
        {
            Refresh();
            return activeIndex >= 0 ? DeviceKey(activeIndex, button) : (KeyCode)((int)KeyCode.JoystickButton0 + button);
        }
        public static string ButtonLabel(int button, GamepadLayout preference)
        {
            var labels = Resolve(preference) == GamepadLayout.PlayStation ? PlayStationButtonLabels : XboxButtonLabels;
            return button >= 0 && button < labels.Length ? labels[button] : "[B" + button + "]";
        }
        private static string[] BuildLabels(string[] names)
        {
            var labels = new string[20];
            for (int i = 0; i < labels.Length; i++)
                labels[i] = (i < names.Length ? names[i] + " " : "") + "[B" + i + "]";
            return labels;
        }
    }
}
