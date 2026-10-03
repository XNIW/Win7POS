using System;
using System.Collections.Generic;

namespace Win7POS.Core.Hardware
{
    public enum ReceiptProfile { Thermal58mm32col, Thermal80mm42col }

    public static class ReceiptProfilePolicy
    {
        public const string Thermal58 = "thermal_58mm_32col";
        public const string Thermal80 = "thermal_80mm_42col";
        public static string Serialize(ReceiptProfile profile) => profile == ReceiptProfile.Thermal58mm32col ? Thermal58 : Thermal80;
        public static bool TryParse(string value, out ReceiptProfile profile)
        {
            profile = ReceiptProfile.Thermal80mm42col;
            if (value == Thermal58) { profile = ReceiptProfile.Thermal58mm32col; return true; }
            return value == Thermal80;
        }
        public static ReceiptProfile FromLegacy(bool use42) => use42 ? ReceiptProfile.Thermal80mm42col : ReceiptProfile.Thermal58mm32col;
        public static int Columns(ReceiptProfile profile) => profile == ReceiptProfile.Thermal58mm32col ? 32 : 42;
        public static int PaperMillimeters(ReceiptProfile profile) => profile == ReceiptProfile.Thermal58mm32col ? 58 : 80;

        /// <summary>Select by measured paper width, independent of localized driver paper names.</summary>
        public static int SelectPaperIndex(ReceiptProfile profile, IReadOnlyList<int> widthsHundredthsOfInch)
        {
            if (widthsHundredthsOfInch == null) return -1;
            var target = profile == ReceiptProfile.Thermal58mm32col ? 228 : 315;
            var tolerance = profile == ReceiptProfile.Thermal58mm32col ? 12 : 16;
            var index = -1;
            var distance = int.MaxValue;
            for (var i = 0; i < widthsHundredthsOfInch.Count; i++)
            {
                var delta = Math.Abs((long)widthsHundredthsOfInch[i] - target);
                if (delta <= tolerance && delta < distance) { index = i; distance = (int)delta; }
            }
            return index; // Driver's default paper remains in effect when no matching form exists.
        }
    }
}
