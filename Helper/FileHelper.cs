using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public static class FileHelper
    {
        /// <summary>
        /// Reads entire file as text and splits by Environment.NewLine chars into a list (non-empty lines).
        /// </summary>
        public static List<string> ReadFile(string filePath)
        {
            var list = new List<string>();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                var txt = sr.ReadToEnd();
                list = txt.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            return list;
        }

        /// <summary>
        /// Reads CSV into list of objects using the CSV header for property mapping.
        /// Header is case-sensitive to property names unless explicitly adjusted.
        /// </summary>
        public static List<T> ReadFileToObject<T>(string filePath, char delimiter) where T : new()
        {
            var list = new List<T>();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                var txt = sr.ReadToEnd();
                var rows = txt.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries).ToList();
                if (rows.Count == 0) return list;

                var headers = rows[0].Replace("\"", "").Split(delimiter);
                foreach (var line in rows.Skip(1))
                {
                    var fields = line.Split(delimiter);
                    var obj = new T();
                    for (int i = 0; i < headers.Length && i < fields.Length; i++)
                    {
                        var prop = typeof(T).GetProperty(headers[i]);
                        if (prop == null) continue;

                        try
                        {
                            var converted = Convert.ChangeType(fields[i], prop.PropertyType);
                            prop.SetValue(obj, converted);
                        }
                        catch
                        {
                            if (prop.PropertyType == typeof(bool))
                            {
                                var b = (fields[i] == "1" || fields[i].Equals("true", StringComparison.OrdinalIgnoreCase));
                                prop.SetValue(obj, b);
                            }
                            else
                            {
                                try { prop.SetValue(obj, fields[i]); } catch { }
                            }
                        }
                    }
                    list.Add(obj);
                }
            }
            return list;
        }
    }
}