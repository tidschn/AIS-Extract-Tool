using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Newtonsoft.Json.Linq;

namespace AisFullExtractor
{
    /// <summary>
    /// Fully generic AIS extractor: walks every section under root.partB.sections
    /// (whatever sectionKeys happen to exist in a given file - tdsTcs, sft, paymentOfTaxes,
    /// demandAndRefund, other-info, or any future Part B section), and for every element
    /// under every section, extracts ALL of its columns using the type/name metadata that
    /// is already embedded in the JSON itself:
    ///
    ///   l2.columnLabel[]      -> plain header-column names (e.g. "Information Category")
    ///   l2.columnDataType[]   -> matching types ("string"/"decimal"/"date"/...)
    ///   l2.columnData[0]      -> the single header row for this element (repeated per detail row)
    ///
    ///   l1.columnLabel[]      -> objects { field, name, type, ... } describing each detail column
    ///   l1.columnData[]       -> one array per detail row, positionally matching l1.columnLabel
    ///
    ///   (elements with no l1/l2 - e.g. paymentOfTaxes - instead have a flat
    ///    columnLabel[] / columnDataType[] / columnData[] directly on the element)
    ///
    /// Elements are grouped by their exact column layout (the ordered list of field names)
    /// so that a section containing multiple differently-shaped element types (as "sft" and
    /// "other-info" both do) produces one sheet per distinct layout, instead of one merged,
    /// misaligned table. Every group's sheet is named from a combination of the section's
    /// sectionKey and its title (or the group's own common element title, when the elements
    /// in that layout group all share one).
    /// </summary>
    public static class AisFullExporter
    {
        public static void Export(string decryptedJson, string outputPath)
        {
            var root = JObject.Parse(decryptedJson);
            var sections = (root.SelectToken("partB.sections") as JArray) ?? new JArray();

            var summaryPlans = new List<SummaryPlan>();
            var sheetPlans = new List<SheetPlan>();
            var categoryElements = new List<(string SectionKey, JObject Element)>();

            foreach (var sectionToken in sections)
            {
                var section = (JObject)sectionToken;
                string sectionKey = (string)section["sectionKey"] ?? "section";
                string sectionTitle = (string)section["title"] ?? "";

                foreach (var unit in CollectElementUnits(section, sectionKey, sectionTitle))
                {
                    if (unit.Elements.Count == 0) continue;

                    // One-row-per-element summary (the portal's top-level list view for this
                    // section, e.g. "Total Rows: 933" for TDS matching the element count) -
                    // only meaningful for elements that actually carry an l2 header row.
                    var summaryPlan = BuildSummaryPlan(unit);
                    if (summaryPlan != null) summaryPlans.Add(summaryPlan);

                    // feed every l2-bearing element into the cross-section category rollup
                    foreach (var el in unit.Elements.Where(e => e["l2"] != null))
                        categoryElements.Add((unit.Key, el));

                    // group this unit's elements by their exact column layout for the detail sheets
                    var groups = new List<(string Signature, List<JObject> Elements)>();
                    foreach (var el in unit.Elements)
                    {
                        string sig = LayoutSignature(el);
                        var existing = groups.FirstOrDefault(g => g.Signature == sig);
                        if (existing.Elements == null)
                            groups.Add((sig, new List<JObject> { el }));
                        else
                            existing.Elements.Add(el);
                    }

                    foreach (var group in groups)
                    {
                        var titles = group.Elements
                            .Select(e => (string)e["title"])
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Distinct()
                            .ToList();
                        string chosenTitle = titles.Count == 1 ? titles[0] : unit.Title;

                        sheetPlans.Add(new SheetPlan
                        {
                            SheetName = BuildSheetName(unit.Key, chosenTitle),
                            Elements = group.Elements
                        });
                    }
                }
            }

            var categoryPlan = BuildCategorySummaryPlan(categoryElements);

            using (var wb = new XLWorkbook())
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Category-wise rollup first (the single highest-level view across every
                // section), then the per-section summaries, then the detail sheets - so they
                // land as the leftmost tabs in that order of increasing detail.
                if (categoryPlan != null)
                {
                    string uniqueName = MakeUniqueSheetName(categoryPlan.SheetName, usedNames);
                    var ws = wb.Worksheets.Add(uniqueName);
                    WriteRowsToSheet(ws, categoryPlan.Headers, categoryPlan.Rows);
                }

                foreach (var plan in summaryPlans)
                {
                    string uniqueName = MakeUniqueSheetName(plan.SheetName, usedNames);
                    var ws = wb.Worksheets.Add(uniqueName);
                    WriteRowsToSheet(ws, plan.Headers, plan.Rows);
                }

                foreach (var plan in sheetPlans)
                {
                    string uniqueName = MakeUniqueSheetName(plan.SheetName, usedNames);
                    WriteGroupSheet(wb, uniqueName, plan.Elements);
                }

                if (wb.Worksheets.Count == 0)
                    throw new InvalidOperationException("No elements were found under partB.sections in the decrypted JSON.");

                wb.SaveAs(outputPath);
            }
        }

        private class SheetPlan
        {
            public string SheetName;
            public List<JObject> Elements;
        }

        private class SummaryPlan
        {
            public string SheetName;
            public List<string> Headers;
            public List<List<object>> Rows;
        }

        private class ElementUnit
        {
            public string Key;
            public string Title;
            public List<JObject> Elements;
        }

        /// <summary>
        /// Most sections carry their elements directly (section.elements[]). Some - so far only
        /// "demandAndRefund" has been observed doing this - instead nest a further level via
        /// section.subSections[], each a mini-section with its own sectionKey/title/elements (or
        /// even more subSections). This walks that structure at arbitrary depth so any similarly
        /// nested section is picked up automatically rather than silently skipped.
        /// </summary>
        private static IEnumerable<ElementUnit> CollectElementUnits(JObject section, string keyPath, string titlePath)
        {
            var elements = (section["elements"] as JArray)?.Cast<JObject>().ToList();
            if (elements != null && elements.Count > 0)
                yield return new ElementUnit { Key = keyPath, Title = titlePath, Elements = elements };

            var subSections = section["subSections"] as JArray;
            if (subSections != null)
            {
                foreach (var subToken in subSections)
                {
                    var sub = (JObject)subToken;
                    string subKey = (string)sub["sectionKey"] ?? "sub";
                    string subTitle = (string)sub["title"] ?? "";
                    string combinedKey = keyPath + "/" + subKey;
                    string combinedTitle = string.IsNullOrEmpty(subTitle) ? titlePath : subTitle;

                    foreach (var unit in CollectElementUnits(sub, combinedKey, combinedTitle))
                        yield return unit;
                }
            }
        }

        // ---------------------------------------------------------------- layout grouping

        private static string LayoutSignature(JObject element)
        {
            var l1 = element.SelectToken("l1.columnLabel") as JArray;
            var l2 = element.SelectToken("l2.columnLabel") as JArray;

            if (l1 != null)
            {
                var l1Fields = l1.Select(c => (string)c["field"]);
                var l2Fields = l2?.Select(c => (string)c) ?? Enumerable.Empty<string>();
                return "L2:" + string.Join(",", l2Fields) + "||L1:" + string.Join(",", l1Fields);
            }

            var flat = element["columnLabel"] as JArray;
            if (flat != null)
                return "FLAT:" + string.Join(",", flat.Select(c => (string)c));

            // Fallback: any other unforeseen shape - group purely by its top-level property names
            return "OTHER:" + string.Join(",", element.Properties().Select(p => p.Name));
        }

        // ---------------------------------------------------------------- summary sheets

        /// <summary>
        /// Builds a one-row-per-element summary for a unit's l2-bearing elements (Information
        /// Category/Code/Description/Source/Count/Amount/...), matching the portal's own
        /// top-level list view for that section - not expanded into per-element detail rows.
        /// Returns null when the unit has no l2 elements (e.g. flat paymentOfTaxes-style
        /// sections, where the detail sheet already is that section's only sensible view).
        /// </summary>
        private static SummaryPlan BuildSummaryPlan(ElementUnit unit)
        {
            var l2Elements = unit.Elements.Where(e => e["l2"] != null).ToList();
            if (l2Elements.Count == 0) return null;

            // union of l2 header columns across all elements, in first-seen order, in case
            // different elements in the same section carry slightly different l2 shapes
            var headerCols = new List<string>();
            foreach (var el in l2Elements)
            {
                var labels = (el.SelectToken("l2.columnLabel") as JArray)?.Select(c => (string)c) ?? Enumerable.Empty<string>();
                foreach (var label in labels)
                    if (!headerCols.Contains(label)) headerCols.Add(label);
            }

            var rows = new List<List<object>>();
            foreach (var el in l2Elements)
            {
                var labels = (el.SelectToken("l2.columnLabel") as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var types = (el.SelectToken("l2.columnDataType") as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var dataRow = el.SelectToken("l2.columnData[0]") as JArray;

                var byLabel = new Dictionary<string, object>();
                for (int i = 0; i < labels.Count; i++)
                {
                    var raw = dataRow != null && i < dataRow.Count ? dataRow[i] : null;
                    string type = i < types.Count ? types[i] : "string";
                    byLabel[labels[i]] = ConvertValue(raw, type);
                }

                var row = new List<object>();
                foreach (var col in headerCols)
                    row.Add(byLabel.ContainsKey(col) ? byLabel[col] : null);
                rows.Add(row);
            }

            return new SummaryPlan
            {
                SheetName = SanitizeSheetName(unit.Key + " Summary"),
                Headers = headerCols,
                Rows = rows
            };
        }

        /// <summary>
        /// Cross-section rollup: one row per distinct (Section, Information Category) pair,
        /// aggregating the number of entries and the total "Count"/"Amount" across every
        /// l2-bearing element in that category - the single highest-level view of the file.
        /// Grouped by section as well as category (not category alone), because the same
        /// category label can legitimately appear under different sections with unrelated
        /// figures (e.g. "Interest from deposit" appears under both tdsTcs and sft).
        /// </summary>
        private static SummaryPlan BuildCategorySummaryPlan(List<(string SectionKey, JObject Element)> elements)
        {
            if (elements.Count == 0) return null;

            var order = new List<(string Section, string Category)>();
            var entries = new Dictionary<(string Section, string Category), int>();
            var totalCount = new Dictionary<(string Section, string Category), double>();
            var totalAmount = new Dictionary<(string Section, string Category), double>();

            foreach (var (sectionKey, el) in elements)
            {
                var labels = (el.SelectToken("l2.columnLabel") as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var types = (el.SelectToken("l2.columnDataType") as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var dataRow = el.SelectToken("l2.columnData[0]") as JArray;

                string category = null;
                double count = 0, amount = 0;
                for (int i = 0; i < labels.Count; i++)
                {
                    var raw = dataRow != null && i < dataRow.Count ? dataRow[i] : null;
                    string type = i < types.Count ? types[i] : "string";
                    var val = ConvertValue(raw, type);

                    if (string.Equals(labels[i], "Information Category", StringComparison.OrdinalIgnoreCase))
                        category = val as string;
                    else if (string.Equals(labels[i], "Count", StringComparison.OrdinalIgnoreCase) && val is double c)
                        count = c;
                    else if (string.Equals(labels[i], "Amount", StringComparison.OrdinalIgnoreCase) && val is double a)
                        amount = a;
                }
                if (string.IsNullOrWhiteSpace(category)) category = "(Uncategorised)";

                var key = (sectionKey, category);
                if (!entries.ContainsKey(key))
                {
                    order.Add(key);
                    entries[key] = 0;
                    totalCount[key] = 0;
                    totalAmount[key] = 0;
                }
                entries[key]++;
                totalCount[key] += count;
                totalAmount[key] += amount;
            }

            var headers = new List<string> { "Section", "Information Category", "No. of Entries", "Total Count", "Total Amount" };
            var rows = order.Select(key => new List<object>
            {
                key.Section, key.Category, (double)entries[key], totalCount[key], totalAmount[key]
            }).ToList();

            return new SummaryPlan { SheetName = "Information Category Summary", Headers = headers, Rows = rows };
        }

        // ---------------------------------------------------------------- sheet writing

        private static void WriteGroupSheet(XLWorkbook wb, string sheetName, List<JObject> elements)
        {
            var first = elements[0];
            bool hasL1L2 = first["l1"] != null && first["l2"] != null;

            var headers = new List<string>();
            var rows = new List<List<object>>();

            if (hasL1L2)
            {
                var l2Labels = (first.SelectToken("l2.columnLabel") as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var l2Types = (first.SelectToken("l2.columnDataType") as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var l1Labels = (first.SelectToken("l1.columnLabel") as JArray)?.ToList() ?? new List<JToken>();

                headers.AddRange(l2Labels);
                headers.AddRange(l1Labels.Select(c => (string)c["name"] ?? (string)c["field"]));

                foreach (var element in elements)
                {
                    var l2Row = element.SelectToken("l2.columnData[0]") as JArray;
                    var headerValues = new List<object>();
                    for (int i = 0; i < l2Labels.Count; i++)
                    {
                        var raw = l2Row != null && i < l2Row.Count ? l2Row[i] : null;
                        string type = i < l2Types.Count ? l2Types[i] : "string";
                        headerValues.Add(ConvertValue(raw, type));
                    }

                    var l1DataRows = (element.SelectToken("l1.columnData") as JArray)?.Cast<JArray>() ?? Enumerable.Empty<JArray>();
                    foreach (var dataRow in l1DataRows)
                    {
                        var row = new List<object>(headerValues);
                        for (int i = 0; i < l1Labels.Count; i++)
                        {
                            var raw = i < dataRow.Count ? dataRow[i] : null;
                            string type = (string)l1Labels[i]["type"] ?? "string";
                            row.Add(ConvertValue(raw, type));
                        }
                        rows.Add(row);
                    }
                }
            }
            else
            {
                var labels = (first["columnLabel"] as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                var types = (first["columnDataType"] as JArray)?.Select(c => (string)c).ToList() ?? new List<string>();
                headers.AddRange(labels);

                foreach (var element in elements)
                {
                    var dataRows = (element["columnData"] as JArray)?.Cast<JArray>() ?? Enumerable.Empty<JArray>();
                    foreach (var dataRow in dataRows)
                    {
                        var row = new List<object>();
                        for (int i = 0; i < labels.Count; i++)
                        {
                            var raw = i < dataRow.Count ? dataRow[i] : null;
                            string type = i < types.Count ? types[i] : "string";
                            row.Add(ConvertValue(raw, type));
                        }
                        rows.Add(row);
                    }
                }
            }

            var ws = wb.Worksheets.Add(sheetName);
            WriteRowsToSheet(ws, headers, rows);
        }

        /// <summary>Shared tail used by both summary and detail sheets: S.No + headers, rows, styling.</summary>
        private static void WriteRowsToSheet(IXLWorksheet ws, List<string> headers, List<List<object>> rows)
        {
            ws.Cell(1, 1).Value = "S.No";
            for (int c = 0; c < headers.Count; c++)
                ws.Cell(1, c + 2).Value = headers[c];

            for (int r = 0; r < rows.Count; r++)
            {
                ws.Cell(r + 2, 1).Value = r + 1;
                for (int c = 0; c < rows[r].Count; c++)
                    SetCellValue(ws.Cell(r + 2, c + 2), rows[r][c]);
            }

            ws.Row(1).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
            AutoSizeColumns(ws, headers, rows);
        }

        /// <summary>
        /// Manual column-width sizing based on character counts. ClosedXML's own
        /// Columns().AdjustToContents() measures actual glyph widths via SixLabors.Fonts, whose
        /// static font-table loader can throw ("type initializer ... threw an exception") in some
        /// Windows environments (locked-down/remote-desktop machines, missing system font access,
        /// certain package/runtime combinations) - a well-known ClosedXML/SixLabors.Fonts issue.
        /// This avoids that dependency entirely: it's a cruder estimate (character count, not
        /// actual rendered pixel width) but never touches the font-measurement codepath.
        /// </summary>
        private static void AutoSizeColumns(IXLWorksheet ws, List<string> headers, List<List<object>> rows)
        {
            ws.Column(1).Width = Math.Max(6, rows.Count.ToString().Length + 2);

            for (int c = 0; c < headers.Count; c++)
            {
                int maxLen = headers[c]?.Length ?? 0;
                foreach (var row in rows)
                {
                    if (c >= row.Count) continue;
                    string text = CellText(row[c]);
                    if (text.Length > maxLen) maxLen = text.Length;
                }
                ws.Column(c + 2).Width = Math.Min(Math.Max(maxLen + 2, 8), 60);
            }
        }

        private static string CellText(object val)
        {
            switch (val)
            {
                case null: return "";
                case DateTime dt: return dt.ToString("dd-MMM-yy");
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                default: return val.ToString();
            }
        }

        // ---------------------------------------------------------------- value/type conversion

        /// <summary>Converts a raw JSON cell using the AIS file's own declared column type.</summary>
        private static object ConvertValue(JToken raw, string type)
        {
            if (raw == null || raw.Type == JTokenType.Null) return null;
            string s = raw.Type == JTokenType.String ? (string)raw : raw.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;

            switch ((type ?? "").ToLowerInvariant())
            {
                case "decimal":
                case "numeric":
                    var cleaned = s.Replace(",", "").Trim();
                    return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                        ? (object)d
                        : s;

                case "date":
                    return DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                        ? (object)dt
                        : (object)s;

                default:
                    return s;
            }
        }

        private static void SetCellValue(IXLCell cell, object val)
        {
            switch (val)
            {
                case null: cell.Value = ""; break;
                case double d: cell.Value = d; break;
                case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "dd-mmm-yy"; break;
                default: cell.Value = val.ToString(); break;
            }
        }

        // ---------------------------------------------------------------- sheet naming

        private static string BuildSheetName(string sectionKey, string title)
        {
            // Strip a leading "Part Bx-" prefix from the title to save characters
            string shortTitle = Regex.Replace(title ?? "", @"^Part\s*B\d+\s*-\s*", "", RegexOptions.IgnoreCase).Trim();
            string combined = string.IsNullOrEmpty(shortTitle) ? sectionKey : sectionKey + " - " + shortTitle;
            return SanitizeSheetName(combined);
        }

        private static string SanitizeSheetName(string name)
        {
            foreach (var ch in new[] { '\\', '/', '*', '?', ':', '[', ']' })
                name = name.Replace(ch, ' ');
            name = name.Trim();
            if (name.Length > 31) name = name.Substring(0, 31).Trim();
            if (string.IsNullOrEmpty(name)) name = "Sheet";
            return name;
        }

        private static string MakeUniqueSheetName(string baseName, HashSet<string> usedNames)
        {
            string candidate = baseName;
            int suffix = 2;
            while (usedNames.Contains(candidate))
            {
                string suffixText = " (" + suffix + ")";
                int maxBaseLen = 31 - suffixText.Length;
                string trimmedBase = baseName.Length > maxBaseLen ? baseName.Substring(0, maxBaseLen) : baseName;
                candidate = trimmedBase + suffixText;
                suffix++;
            }
            usedNames.Add(candidate);
            return candidate;
        }
    }
}
