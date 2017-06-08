﻿//------------------------------------------------------------------------------
// <copyright file="ExcelDataReader.cs"
//  company="Scott Dorman"
//  library="Cadru">
//    Copyright (C) 2001-2017 Scott Dorman.
// </copyright>
//
// <license>
//    Licensed under the Microsoft Public License (Ms-PL) (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//    http://opensource.org/licenses/Ms-PL.html
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
// </license>
//------------------------------------------------------------------------------

namespace Cadru.Data.Excel
{
    using DocumentFormat.OpenXml;
    using DocumentFormat.OpenXml.Packaging;
    using DocumentFormat.OpenXml.Spreadsheet;
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;

    public partial class ExcelDataReader : IDataReader
    {
        public int Depth => 0;

        public int RecordsAffected => -1;

        public void Close() => Dispose();

        public void Dispose()
        {
            if (this.reader != null)
            {
                this.reader.Dispose();
            }

            if (this.document != null)
            {
                this.document.Dispose();
            }
        }

        public DataTable GetSchemaTable() => throw new NotSupportedException();

        public bool NextResult()
        {
            if (this.currentIndex >= this.ResultsCount - 1)
            {
                return false;
            }

            this.currentIndex++;
            Reset();
            return true;
        }

        public bool Read()
        {
            if (this.firstRead)
            {
                this.currentSheet = GetSheetByIndex(this.currentIndex);
                var currentWorksheetPart = this.document.WorkbookPart.GetPartById(this.CurrentSheetId);
                this.reader = OpenXmlReader.Create(currentWorksheetPart);
                SkipRows(GetEmptyRowsCount(currentWorksheetPart));
                this.headers = this.FirstRowAsHeader ? GetFirstRowAsHeaders(currentWorksheetPart) : GetRangeHeaders(currentWorksheetPart);
                this.firstRead = false;
            }

            OpenXmlElement currentRow = null;

            while (this.reader.Read())
            {
                if (this.reader.ElementType == typeof(Row))
                {
                    currentRow = this.reader.LoadCurrentElement();
                    if (IsRowEmpty(currentRow))
                    {
                        continue;
                    }

                    this.currentRowData = AdjustRow(currentRow, this.headers.Count);
                    break;
                }
            }

            return currentRow != null && !this.reader.EOF;
        }

        private bool IsRowEmpty(OpenXmlElement row)
        {
            return String.IsNullOrEmpty(row.InnerText);
        }

        private static IEnumerable<Cell> AdjustRow(OpenXmlElement row, int capacity)
        {
            if (row == null)
            {
                return new Cell[] { };
            }

            var cells = row.Elements<Cell>().ToArray();
            if (capacity == -1)
            {
                capacity = cells.Count();
            }

            var list = cells.OrderBy(x => GetColumnIndexByName(x.CellReference.Value)).Take(capacity).ToList();
            while (list.Count() < capacity)
            {
                list.Add(new Cell());
            }

            return list;
        }

        private string GetCellValue(CellType cell)
        {
            if (cell == null || cell.CellValue == null)
            {
                return null;
            }

            var value = cell.CellValue.InnerXml;
            if (value == null)
            {
                return null;
            }

            if (Int32.TryParse(value, out int index) && cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
            {
                return this.sharedStrings[index];
            }

            return value;
        }

        private int GetEmptyRowsCount(OpenXmlPart worksheetPart)
        {
            var emptyRowsCount = 0;
            using (var reader = OpenXmlReader.Create(worksheetPart))
            {
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(Row))
                    {
                        var row = reader.LoadCurrentElement();
                        if (!IsRowEmpty(row))
                        {
                            break;
                        }

                        emptyRowsCount++;
                    }
                }
            }

            return emptyRowsCount;
        }

        private IList<string> GetFirstRowAsHeaders(OpenXmlPart worksheetPart)
        {
            var result = new List<string>();
            using (var reader = OpenXmlReader.Create(worksheetPart))
            {
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(Row))
                    {
                        result = AdjustRow(reader.LoadCurrentElement(), -1).Select(this.GetCellValue).ToList();
                        break;
                    }
                }
            }

            SkipRow();
            return result;
        }

        private static IList<string> GetRangeHeaders(OpenXmlPart worksheetPart)
        {
            var count = 0;
            using (var reader = OpenXmlReader.Create(worksheetPart))
            {
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(Row))
                    {
                        count = reader.LoadCurrentElement().Elements<Cell>().Count();
                        break;
                    }
                }
            }

            return Enumerable.Range(0, count).Select(x => "col" + x).ToArray();
        }

        private Sheet GetSheetByIndex(int sheetIndex)
        {
            return this.sheets.ElementAtOrDefault(sheetIndex);
        }

        private Sheet GetSheetByName(string sheetName)
        {
            return this.sheets.FirstOrDefault(x => x.Name == sheetName);
        }

        private IList<Sheet> GetSheets(SpreadsheetDocument document)
        {
            return document.WorkbookPart.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().ToList();
        }

        public static int GetColumnIndexByName(string colName)
        {
            var name = GetStartingLettersOnly(colName);
            int number = 0, pow = 1;
            for (var i = name.Length - 1; i >= 0; i--)
            {
                number += (name[i] - 'A' + 1) * pow;
                pow *= 26;
            }

            return number - 1;
        }

        private static string GetStartingLettersOnly(string colName)
        {
            var result = String.Empty;
            foreach (var ch in colName)
            {
                if (Char.IsLetter(ch))
                {
                    result += ch;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private void Reset()
        {
            this.currentRowData = null;
            this.headers?.Clear();
            this.firstRead = true;
        }

        private void SkipRow()
        {
            while (this.reader.Read())
            {
                if (this.reader.ElementType == typeof(Row) && this.reader.IsEndElement)
                {
                    break;
                }
            }
        }

        private void SkipRows(int count)
        {
            for (var i = 0; i < count; i++)
            {
                SkipRow();
            }
        }
    }
}