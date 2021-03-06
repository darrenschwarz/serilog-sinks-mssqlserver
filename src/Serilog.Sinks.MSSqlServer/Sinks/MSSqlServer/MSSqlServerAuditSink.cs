﻿// Copyright 2018 Serilog Contributors 
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using Microsoft.Azure.Services.AppAuthentication;

namespace Serilog.Sinks.MSSqlServer
{
    /// <summary>
    ///  Writes log events as rows in a table of MSSqlServer database using Audit logic, meaning that each row is synchronously committed
    ///  and any errors that occur are propagated to the caller.
    /// </summary>
    public class MSSqlServerAuditSink : ILogEventSink, IDisposable
    {
        private readonly MSSqlServerSinkTraits _traits;

        /// <summary>
        ///     Construct a sink posting to the specified database.
        /// </summary>
        /// <param name="connectionString">Connection string to access the database.</param>
        /// <param name="tableName">Name of the table to store the data in.</param>
        /// <param name="schemaName">Name of the schema for the table to store the data in. The default is 'dbo'.</param>
        /// <param name="formatProvider">Supplies culture-specific formatting information, or null.</param>
        /// <param name="autoCreateSqlTable">Create log table with the provided name on destination sql server.</param>
        /// <param name="columnOptions">Options that pertain to columns</param>
        /// <param name="useMsi">Option to use MSI</param>
        /// <param name="azureServiceTokenProviderResource">Resource required in AzureServiceTokenProvider.GetAccessTokenAsync(azureServiceTokenProviderResource). This will error if null, and useMsi is st to true</param>
        public MSSqlServerAuditSink(
            string connectionString,
            string tableName,
            IFormatProvider formatProvider,
            bool autoCreateSqlTable = false,
            ColumnOptions columnOptions = null,
            string schemaName = "dbo",
            bool useMsi = false,
            string azureServiceTokenProviderResource = null)
        {
            columnOptions.FinalizeConfigurationForSinkConstructor();

#pragma warning disable S2589 // Boolean expressions should not be gratuitous
            if (columnOptions != null)
#pragma warning restore S2589 // Boolean expressions should not be gratuitous
            {
#pragma warning disable S1066 // Collapsible "if" statements should be merged
                if (columnOptions.DisableTriggers)
#pragma warning restore S1066 // Collapsible "if" statements should be merged
                    throw new NotSupportedException($"The {nameof(ColumnOptions.DisableTriggers)} option is not supported for auditing.");
            }

            _traits = new MSSqlServerSinkTraits(connectionString, tableName, schemaName, columnOptions, formatProvider, autoCreateSqlTable, useMsi, azureServiceTokenProviderResource);

        }

        /// <summary>Emit the provided log event to the sink.</summary>
        /// <param name="logEvent">The log event to write.</param>
        public void Emit(LogEvent logEvent)
        {
            try
            {
                using (var cn = new SqlConnection(_traits.connectionString))
                {
                    if (_traits.useMsi)
                    {
                        cn.AccessToken = new AzureServiceTokenProvider()
                            .GetAccessTokenAsync(_traits.azureServiceTokenProviderResource).Result;
                    }

                    cn.Open();
                    using (SqlCommand command = cn.CreateCommand())
                    {
                        command.CommandType = CommandType.Text;

                        StringBuilder fieldList = new StringBuilder($"INSERT INTO [{_traits.schemaName}].[{_traits.tableName}] (");
                        StringBuilder parameterList = new StringBuilder(") VALUES (");

                        int index = 0;
                        foreach (var field in _traits.GetColumnsAndValues(logEvent))
                        {
                            if (index != 0)
                            {
                                fieldList.Append(',');
                                parameterList.Append(',');
                            }

                            fieldList.Append(field.Key);
                            parameterList.Append("@P");
                            parameterList.Append(index);

                            SqlParameter parameter = new SqlParameter($"@P{index}", field.Value ?? DBNull.Value);

                            // The default is SqlDbType.DateTime, which will truncate the DateTime value if the actual
                            // type in the database table is datetime2. So we explicitly set it to DateTime2, which will
                            // work both if the field in the table is datetime and datetime2, which is also consistent with 
                            // the behavior of the non-audit sink.
                            if (field.Value is DateTime)
                                parameter.SqlDbType = SqlDbType.DateTime2;

                            command.Parameters.Add(parameter);

                            index++;
                        }

                        parameterList.Append(')');
                        fieldList.Append(parameterList.ToString());

                        command.CommandText = fieldList.ToString();

                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Unable to write log event to the database due to following error: {1}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Releases the unmanaged resources used by the Serilog.Sinks.MSSqlServer.MSSqlServerAuditSink and optionally
        /// releases the managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged
        ///                         resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _traits.Dispose();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
