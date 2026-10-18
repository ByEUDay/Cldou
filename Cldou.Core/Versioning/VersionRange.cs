using System;
using System.Collections.Generic;
using System.Text;

namespace Cldou.Core.Versioning
{
    /// <summary>
    /// 版本约束表达式（面向 mod 依赖声明场景的实用子集）。
    ///
    /// 语法：
    ///   *        任意版本
    ///   1.2.3    精确版本（等价 =1.2.3）
    ///   1.2      范围 [1.2.0, 1.3.0)
    ///   1        范围 [1.0.0, 2.0.0)
    ///   1.2.x    范围 [1.2.0, 1.3.0)；1.x → [1.0.0, 2.0.0)
    ///   >=1.2  >1.2  <2  <=2  =1.2.3
    ///   ^1.2.3   兼容范围 [1.2.3, 2.0.0)
    ///   ^0.2.3   [0.2.3, 0.3.0)；^0.0.3 → [0.0.3, 0.0.4)
    ///   ~1.2.3   [1.2.3, 1.3.0)；~1 → [1.0.0, 2.0.0)
    ///   空格     与（AND）：">=1.0 <2.0"
    ///   ||       或（OR）： "1.2.3 || >=2.0"
    ///
    /// prerelease 规则（npm 风格）：若被检查版本带 prerelease，
    /// 只有当同组比较器中存在相同 主.次.补 的 prerelease 版本时才视为满足。
    /// </summary>
    public sealed class VersionRange
    {
        private struct Comparator
        {
            public string Op;         // "=", ">", ">=", "<", "<="
            public SemVersion Version;

            public bool Matches(SemVersion v)
            {
                int cmp = v.CompareTo(Version);
                switch (Op)
                {
                    case "=": return cmp == 0;
                    case ">": return cmp > 0;
                    case ">=": return cmp >= 0;
                    case "<": return cmp < 0;
                    case "<=": return cmp <= 0;
                    default: return false;
                }
            }
        }

        private readonly List<Comparator[]> _orGroups;

        public string Original { get; private set; }

        private VersionRange(List<Comparator[]> groups, string original)
        {
            _orGroups = groups;
            Original = original;
        }

        public static VersionRange Any
        {
            get { return new VersionRange(new List<Comparator[]> { new Comparator[0] }, "*"); }
        }

        public static VersionRange Parse(string expression)
        {
            VersionRange range;
            string error;
            if (!TryParse(expression, out range, out error))
            {
                throw new FormatException(error);
            }
            return range;
        }

        public static bool TryParse(string expression, out VersionRange range, out string error)
        {
            range = null;
            error = null;

            if (expression == null)
            {
                error = "版本约束为 null";
                return false;
            }

            string text = expression.Trim();
            if (text.Length == 0)
            {
                error = "版本约束为空字符串";
                return false;
            }

            var groups = new List<Comparator[]>();
            string[] orParts = text.Split(new[] { "||" }, StringSplitOptions.None);

            foreach (string rawGroup in orParts)
            {
                string group = rawGroup.Trim();
                if (group.Length == 0)
                {
                    error = "版本约束存在空分组（检查 '||' 的使用）：'" + expression + "'";
                    return false;
                }

                var groupComparators = new List<Comparator>();
                string[] terms = group.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string term in terms)
                {
                    List<Comparator> termComparators;
                    string termError;
                    if (!TryParseTerm(term, out termComparators, out termError))
                    {
                        error = termError + "（于约束 '" + expression + "' 的项 '" + term + "'）";
                        return false;
                    }
                    if (termComparators != null)
                    {
                        groupComparators.AddRange(termComparators);
                    }
                }

                groups.Add(groupComparators.ToArray());
            }

            range = new VersionRange(groups, text);
            return true;
        }

        private static bool TryParseTerm(string term, out List<Comparator> comparators, out string error)
        {
            comparators = null;
            error = null;

            if (term == "*" || term == "x" || term == "X")
            {
                comparators = new List<Comparator>();
                return true;
            }

            string op = "";
            string rest = term;

            if (term.StartsWith(">=") || term.StartsWith("<="))
            {
                op = term.Substring(0, 2);
                rest = term.Substring(2);
            }
            else if (term.Length > 0 && (term[0] == '>' || term[0] == '<' || term[0] == '=' || term[0] == '^' || term[0] == '~'))
            {
                op = term.Substring(0, 1);
                rest = term.Substring(1);
            }

            rest = rest.Trim();
            if (rest.Length == 0)
            {
                error = "约束项缺少版本号";
                return false;
            }

            // 通配符（仅允许出现在无操作符或以 = 开头时）
            if (ContainsWildcard(rest))
            {
                if (op.Length > 0 && op != "=")
                {
                    error = "通配符不能与 '" + op + "' 组合使用";
                    return false;
                }

                List<Comparator> wild;
                string wildError;
                if (!TryParseWildcard(rest, out wild, out wildError))
                {
                    error = wildError;
                    return false;
                }
                comparators = wild;
                return true;
            }

            switch (op)
            {
                case "^":
                    return TryBuildCaret(rest, out comparators, out error);
                case "~":
                    return TryBuildTilde(rest, out comparators, out error);
                case "":
                    // 无操作符：完整版本为精确；部分版本为区间
                    return TryBuildBare(rest, out comparators, out error);
                default:
                    {
                        SemVersion version;
                        string parseError;
                        if (!SemVersion.TryParse(rest, out version, out parseError))
                        {
                            error = parseError;
                            return false;
                        }
                        comparators = new List<Comparator> { new Comparator { Op = op, Version = version } };
                        return true;
                    }
            }
        }

        private static bool ContainsWildcard(string text)
        {
            foreach (char c in text)
            {
                if (c == 'x' || c == 'X' || c == '*') return true;
            }
            return false;
        }

        private static bool TryParseWildcard(string rest, out List<Comparator> comparators, out string error)
        {
            comparators = null;
            error = null;

            string[] parts = rest.Split('.');
            if (parts.Length > 3)
            {
                error = "通配符版本段过多";
                return false;
            }

            int[] numeric = new int[3];
            int wildcardAt = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part == "x" || part == "X" || part == "*")
                {
                    if (wildcardAt < 0) wildcardAt = i;
                    continue;
                }

                if (wildcardAt >= 0)
                {
                    error = "通配符之后不能再出现数字段（'" + rest + "'）";
                    return false;
                }

                int value;
                if (!int.TryParse(part, out value) || value < 0)
                {
                    error = "通配符版本段非法：'" + part + "'";
                    return false;
                }
                numeric[i] = value;
            }

            if (wildcardAt < 0)
            {
                error = "内部错误：通配符解析未找到通配段";
                return false;
            }

            if (wildcardAt == 0)
            {
                comparators = new List<Comparator>(); // 任意版本
                return true;
            }

            // 下界：把通配符前一段固定，其余补 0
            var lower = new SemVersion[1];
            lower[0] = MakeVersion(numeric[0], wildcardAt > 1 ? numeric[1] : 0, 0);

            // 上界：把“最后存在的数字段”+1
            int bumpIndex = wildcardAt - 1;
            int major = numeric[0];
            int minor = bumpIndex >= 1 ? numeric[1] : 0;
            int patch = bumpIndex >= 2 ? numeric[2] : 0;

            if (bumpIndex == 0) { major += 1; minor = 0; patch = 0; }
            else if (bumpIndex == 1) { minor += 1; patch = 0; }
            else { patch += 1; }

            comparators = new List<Comparator>
            {
                new Comparator { Op = ">=", Version = lower[0] },
                new Comparator { Op = "<", Version = MakeVersion(major, minor, patch) }
            };
            return true;
        }

        private static bool TryBuildBare(string rest, out List<Comparator> comparators, out string error)
        {
            comparators = null;
            error = null;

            string[] parts = rest.Split('.');

            if (parts.Length == 3)
            {
                SemVersion version;
                string parseError;
                if (!SemVersion.TryParse(rest, out version, out parseError))
                {
                    error = parseError;
                    return false;
                }
                comparators = new List<Comparator> { new Comparator { Op = "=", Version = version } };
                return true;
            }

            if (parts.Length == 1 || parts.Length == 2)
            {
                // "1" → [1.0.0, 2.0.0)；"1.2" → [1.2.0, 1.3.0)
                int major, minor = 0;
                if (!int.TryParse(parts[0], out major) || major < 0)
                {
                    error = "版本段非法：'" + parts[0] + "'";
                    return false;
                }
                if (parts.Length == 2 && !int.TryParse(parts[1], out minor) || (parts.Length == 2 && minor < 0))
                {
                    error = "版本段非法：'" + parts[1] + "'";
                    return false;
                }

                int upperMajor = major + (parts.Length == 1 ? 1 : 0);
                int upperMinor = parts.Length == 1 ? 0 : minor + 1;

                comparators = new List<Comparator>
                {
                    new Comparator { Op = ">=", Version = MakeVersion(major, minor, 0) },
                    new Comparator { Op = "<", Version = MakeVersion(upperMajor, upperMinor, 0) }
                };
                return true;
            }

            error = "版本段数量非法：'" + rest + "'";
            return false;
        }

        private static bool TryBuildCaret(string rest, out List<Comparator> comparators, out string error)
        {
            comparators = null;
            error = null;

            SemVersion baseVersion;
            string parseError;
            if (!SemVersion.TryParse(rest, out baseVersion, out parseError))
            {
                error = parseError;
                return false;
            }

            int upperMajor = baseVersion.Major;
            int upperMinor = baseVersion.Minor;
            int upperPatch = baseVersion.Patch;

            if (baseVersion.Major > 0)
            {
                upperMajor += 1;
                upperMinor = 0;
                upperPatch = 0;
            }
            else if (baseVersion.Minor > 0)
            {
                upperMinor += 1;
                upperPatch = 0;
            }
            else
            {
                upperPatch += 1;
            }

            comparators = new List<Comparator>
            {
                new Comparator { Op = ">=", Version = baseVersion },
                new Comparator { Op = "<", Version = MakeVersion(upperMajor, upperMinor, upperPatch) }
            };
            return true;
        }

        private static bool TryBuildTilde(string rest, out List<Comparator> comparators, out string error)
        {
            comparators = null;
            error = null;

            SemVersion baseVersion;
            string parseError;
            if (!SemVersion.TryParse(rest, out baseVersion, out parseError))
            {
                error = parseError;
                return false;
            }

            int upperMajor = baseVersion.Major;
            int upperMinor = baseVersion.Minor + 1;
            int upperPatch = 0;

            if (baseVersion.Minor == 0 && rest.Split('.').Length == 1)
            {
                // "~1" → [1.0.0, 2.0.0)
                upperMajor += 1;
                upperMinor = 0;
            }

            comparators = new List<Comparator>
            {
                new Comparator { Op = ">=", Version = baseVersion },
                new Comparator { Op = "<", Version = MakeVersion(upperMajor, upperMinor, upperPatch) }
            };
            return true;
        }

        private static SemVersion MakeVersion(int major, int minor, int patch)
        {
            return SemVersion.Parse(major + "." + minor + "." + patch);
        }

        public bool Contains(SemVersion version)
        {
            if (version == null)
            {
                return false;
            }

            foreach (Comparator[] group in _orGroups)
            {
                bool all = true;
                bool allowsPrerelease = !version.IsPrerelease;

                foreach (Comparator comparator in group)
                {
                    if (!comparator.Matches(version))
                    {
                        all = false;
                        break;
                    }

                    if (version.IsPrerelease
                        && comparator.Version.IsPrerelease
                        && comparator.Version.Major == version.Major
                        && comparator.Version.Minor == version.Minor
                        && comparator.Version.Patch == version.Patch)
                    {
                        allowsPrerelease = true;
                    }
                }

                if (all && allowsPrerelease)
                {
                    return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            return Original;
        }
    }
}
