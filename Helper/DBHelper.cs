using NLog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public static class DBHelper
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public static async Task ExecuteNonQuery(string conn, string sql)
        {
            using (var c = new SqlConnection(conn))
            {
                await c.OpenAsync();
                using (var cmd = new SqlCommand(sql, c))
                {
                    cmd.CommandTimeout = 1800;
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task<DataTable> Execute(string conn, string sql)
        {
            var dt = new DataTable();

            using (var c = new SqlConnection(conn))
            {
                await c.OpenAsync();

                using (var cmd = new SqlCommand(sql, c))
                {
                    cmd.CommandTimeout = 1800;

                    using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
                    {
                        if (reader.HasRows)
                        {
                            dt.Load(reader);
                        }
                        else
                        {
                            // Create an empty shape if no rows but columns exist
                            for (int i = 0; i < reader.FieldCount; i++)
                                dt.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
                        }
                    }
                }
            }

            return dt;
        }

        public static async Task BulkInsert(string conn, DataTable dataTable, string tableName)
        {
            using (var c = new SqlConnection(conn))
            {
                await c.OpenAsync();

                using (var bc = new SqlBulkCopy(c))
                {
                    bc.DestinationTableName = tableName;
                    bc.BatchSize = 10000;
                    bc.BulkCopyTimeout = 300;

                    foreach (DataColumn col in dataTable.Columns)
                        bc.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                    try
                    {
                        await bc.WriteToServerAsync(dataTable);
                    }
                    catch (SqlException ex)
                    {
                        // Debug the common "invalid column length" in SqlBulkCopy
                        if (ex.Message.Contains("invalid column length"))
                        {
                            try
                            {
                                var match = Regex.Match(ex.Message, @"\d+");
                                int colid = Convert.ToInt32(match.Value) - 1;

                                FieldInfo fi = typeof(SqlBulkCopy).GetField("_sortedColumnMappings",
                                    BindingFlags.NonPublic | BindingFlags.Instance);
                                var sortedColumns = fi.GetValue(bc);
                                var items = (object[])sortedColumns.GetType()
                                    .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)
                                    .GetValue(sortedColumns);
                                FieldInfo itemData = items[colid].GetType()
                                    .GetField("_metadata", BindingFlags.NonPublic | BindingFlags.Instance);
                                var metadata = itemData.GetValue(items[colid]);
                                var column = metadata.GetType()
                                    .GetField("column", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    .GetValue(metadata);
                                var length = metadata.GetType()
                                    .GetField("length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    .GetValue(metadata);

                                _log.Error($"BulkInsert column length issue. Column: {column}, length: {length}");
                            }
                            catch { /* ignore */ }
                        }

                        _log.Error($"BulkInsert error into {tableName}: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        public static DataTable ConvertToDataTable<T>(IEnumerable<T> list)
        {
            var dt = new DataTable();
            var props = typeof(T).GetProperties();

            foreach (var p in props)
            {
                var propType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                dt.Columns.Add(p.Name, propType);
            }

            foreach (var item in list)
            {
                var row = dt.NewRow();
                foreach (var p in props)
                {
                    try
                    {
                        var val = p.GetValue(item);
                        if (val is DateTime dtv && dtv < new DateTime(1753, 1, 1))
                            row[p.Name] = DBNull.Value;
                        else
                            row[p.Name] = val ?? DBNull.Value;
                    }
                    catch
                    {
                        row[p.Name] = DBNull.Value;
                    }
                }
                dt.Rows.Add(row);
            }

            return dt;
        }
    }
}