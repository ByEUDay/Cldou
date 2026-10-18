using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cldou.Core.Versioning
{
    /// <summary>
    /// 语义化版本（SemVer 2.0 优先级规则的实现）。
    ///
    /// 宽松解析：允许 1~3 段数字（"1" / "1.2" / "1.2.3"），允许可选前缀 'v'，
    /// 允许 -prerelease 与 +build 后缀。比较遵循 SemVer 2.0：
    ///   - 数字段逐位比较；
    ///   - 有 prerelease 的版本低于无 prerelease 的版本；
    ///   - prerelease 标识符按“数字 < 字母数字”规则逐段比较。
    /// </summary>
    public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
    {
        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }

        /// <summary>prerelease 标识符串（不含 '-'），无则 null。</summary>
        public string Prerelease { get; private set; }

        /// <summary>build 元数据（不含 '+'），无则 null。不参与比较。</summary>
        public string Build { get; private set; }

        public bool IsPrerelease
        {
            get { return !string.IsNullOrEmpty(Prerelease); }
        }

        private SemVersion(int major, int minor, int patch, string prerelease, string build)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = string.IsNullOrEmpty(prerelease) ? null : prerelease;
            Build = string.IsNullOrEmpty(build) ? null : build;
        }

        public static SemVersion Parse(string text)
        {
            SemVersion version;
            string error;
            if (!TryParse(text, out version, out error))
            {
                throw new FormatException(error);
            }
            return version;
        }

        public static bool TryParse(string text, out SemVersion version)
        {
            string error;
            return TryParse(text, out version, out error);
        }

        public static bool TryParse(string text, out SemVersion version, out string error)
        {
            version = null;
            error = null;

            if (string.IsNullOrEmpty(text))
            {
                error = "版本号为空";
                return false;
            }

            string s = text.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            {
                s = s.Substring(1).Trim();
            }

            // 拆分 build / prerelease
            string build = null;
            int buildIndex = s.IndexOf('+');
            if (buildIndex >= 0)
            {
                build = s.Substring(buildIndex + 1);
                s = s.Substring(0, buildIndex);
                if (build.Length == 0)
                {
                    error = "版本号 build 元数据为空：'" + text + "'";
                    return false;
                }
            }

            string prerelease = null;
            int preIndex = s.IndexOf('-');
            if (preIndex >= 0)
            {
                prerelease = s.Substring(preIndex + 1);
                s = s.Substring(0, preIndex);
                if (prerelease.Length == 0)
                {
                    error = "版本号 prerelease 为空：'" + text + "'";
                    return false;
                }
            }

            string[] parts = s.Split('.');
            if (parts.Length < 1 || parts.Length > 3)
            {
                error = "版本号数字段数量非法（应为 1~3 段）：'" + text + "'";
                return false;
            }

            int major, minor = 0, patch = 0;
            if (!TryParseNumericPart(parts[0], text, out major)) { error = "版本号主段非法：'" + text + "'"; return false; }
            if (parts.Length > 1 && !TryParseNumericPart(parts[1], text, out minor)) { error = "版本号次段非法：'" + text + "'"; return false; }
            if (parts.Length > 2 && !TryParseNumericPart(parts[2], text, out patch)) { error = "版本号补丁段非法：'" + text + "'"; return false; }

            if (prerelease != null)
            {
                foreach (string identifier in prerelease.Split('.'))
                {
                    if (!IsValidIdentifier(identifier))
                    {
                        error = "prerelease 标识符非法：'" + identifier + "'（" + text + "）";
                        return false;
                    }
                }
            }

            if (build != null)
            {
                foreach (string identifier in build.Split('.'))
                {
                    if (!IsValidIdentifier(identifier))
                    {
                        error = "build 标识符非法：'" + identifier + "'（" + text + "）";
                        return false;
                    }
                }
            }

            version = new SemVersion(major, minor, patch, prerelease, build);
            return true;
        }

        private static bool TryParseNumericPart(string part, string source, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(part)) return false;
            if (part.Length > 1 && part[0] == '0')
            {
                // 允许 "0" 本身；禁止前导零（SemVer 规则）
                return false;
            }
            return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsValidIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return false;
            foreach (char c in identifier)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        public int CompareTo(SemVersion other)
        {
            if (other == null) return 1;

            int cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;

            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;

            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        private static int ComparePrerelease(string a, string b)
        {
            bool aEmpty = string.IsNullOrEmpty(a);
            bool bEmpty = string.IsNullOrEmpty(b);

            // 无 prerelease > 有 prerelease
            if (aEmpty && bEmpty) return 0;
            if (aEmpty) return 1;
            if (bEmpty) return -1;

            string[] aParts = a.Split('.');
            string[] bParts = b.Split('.');
            int count = Math.Min(aParts.Length, bParts.Length);

            for (int i = 0; i < count; i++)
            {
                string x = aParts[i];
                string y = bParts[i];

                long xn, yn;
                bool xNum = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out xn);
                bool yNum = long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out yn);

                if (xNum && yNum)
                {
                    int cmp = xn.CompareTo(yn);
                    if (cmp != 0) return cmp;
                }
                else if (xNum)
                {
                    return -1; // 数字 < 字母数字
                }
                else if (yNum)
                {
                    return 1;
                }
                else
                {
                    int cmp = string.CompareOrdinal(x, y);
                    if (cmp != 0) return cmp < 0 ? -1 : 1;
                }
            }

            return aParts.Length.CompareTo(bParts.Length);
        }

        public bool Equals(SemVersion other)
        {
            return other != null && CompareTo(other) == 0;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as SemVersion);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Major * 397;
                hash = (hash ^ Minor) * 397;
                hash = (hash ^ Patch) * 397;
                hash = (hash ^ (Prerelease != null ? Prerelease.GetHashCode() : 0)) * 397;
                return hash;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder(16);
            sb.Append(Major).Append('.').Append(Minor).Append('.').Append(Patch);
            if (Prerelease != null)
            {
                sb.Append('-').Append(Prerelease);
            }
            if (Build != null)
            {
                sb.Append('+').Append(Build);
            }
            return sb.ToString();
        }

        public static bool operator <(SemVersion a, SemVersion b) { return Compare(a, b) < 0; }
        public static bool operator >(SemVersion a, SemVersion b) { return Compare(a, b) > 0; }
        public static bool operator <=(SemVersion a, SemVersion b) { return Compare(a, b) <= 0; }
        public static bool operator >=(SemVersion a, SemVersion b) { return Compare(a, b) >= 0; }
        public static bool operator ==(SemVersion a, SemVersion b) { return Compare(a, b) == 0; }
        public static bool operator !=(SemVersion a, SemVersion b) { return Compare(a, b) != 0; }

        private static int Compare(SemVersion a, SemVersion b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if ((object)a == null) return -1;
            if ((object)b == null) return 1;
            return a.CompareTo(b);
        }
    }
}
