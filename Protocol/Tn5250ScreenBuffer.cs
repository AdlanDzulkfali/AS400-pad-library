using System;
using System.Collections.Generic;
using System.Text;

namespace AS400Automation.Protocol
{
    /// <summary>
    /// Thread-safe virtual presentation space representing an IBM 5250 terminal screen.
    /// Manages 24x80 character cells, attribute codes, field definitions, and cursor tracking.
    /// </summary>
    public class Tn5250ScreenBuffer
    {
        public int Rows { get; }
        public int Columns { get; }
        public int TotalSize { get; }

        private readonly char[] _cells;
        private readonly byte[] _attributes;
        private readonly bool[] _isAttribute;
        private readonly List<Tn5250Field> _fields = new List<Tn5250Field>();
        private readonly object _lock = new object();

        private int _cursorRow = 1;
        private int _cursorCol = 1;

        public int CursorRow
        {
            get { lock (_lock) { return _cursorRow; } }
            set { lock (_lock) { _cursorRow = Math.Max(1, Math.Min(Rows, value)); } }
        }

        public int CursorCol
        {
            get { lock (_lock) { return _cursorCol; } }
            set { lock (_lock) { _cursorCol = Math.Max(1, Math.Min(Columns, value)); } }
        }

        public Tn5250ScreenBuffer(int rows = Tn5250Constants.SCREEN_ROWS, int columns = Tn5250Constants.SCREEN_COLS)
        {
            Rows = rows > 0 ? rows : Tn5250Constants.SCREEN_ROWS;
            Columns = columns > 0 ? columns : Tn5250Constants.SCREEN_COLS;
            TotalSize = Rows * Columns;

            _cells = new char[TotalSize];
            _attributes = new byte[TotalSize];
            _isAttribute = new bool[TotalSize];

            Clear();
        }

        /// <summary>
        /// Clears all characters and attributes in the presentation space, resetting cursor to (1, 1).
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                for (int i = 0; i < TotalSize; i++)
                {
                    _cells[i] = ' ';
                    _attributes[i] = 0;
                    _isAttribute[i] = false;
                }
                _fields.Clear();
                _cursorRow = 1;
                _cursorCol = 1;
            }
        }

        /// <summary>
        /// Validates 1-based coordinates and throws AS400Exception if out of bounds.
        /// </summary>
        public void ValidateCoordinates(int row, int col)
        {
            if (row < 1 || row > Rows)
                throw new AS400Exception(AS400ErrorCode.InvalidCoordinate,
                    $"Row '{row}' is out of range. Must be between 1 and {Rows}.");
            if (col < 1 || col > Columns)
                throw new AS400Exception(AS400ErrorCode.InvalidCoordinate,
                    $"Column '{col}' is out of range. Must be between 1 and {Columns}.");
        }

        /// <summary>
        /// Sets cursor to 1-based (row, col) coordinates with strict validation.
        /// </summary>
        public void SetCursor(int row, int col)
        {
            ValidateCoordinates(row, col);
            lock (_lock)
            {
                _cursorRow = row;
                _cursorCol = col;
            }
        }

        /// <summary>
        /// Retrieves the current cursor coordinate as a tuple (Row, Col).
        /// </summary>
        public (int Row, int Col) GetCursorPosition()
        {
            lock (_lock)
            {
                return (_cursorRow, _cursorCol);
            }
        }

        private int ToIndex(int row, int col)
        {
            return ((row - 1) * Columns) + (col - 1);
        }

        private void FromIndex(int index, out int row, out int col)
        {
            row = (index / Columns) + 1;
            col = (index % Columns) + 1;
        }

        /// <summary>
        /// Writes a character at the current cursor position and advances the cursor.
        /// </summary>
        public void WriteCharAndAdvance(char c, byte attribute = 0, bool isAttribute = false)
        {
            lock (_lock)
            {
                int index = ToIndex(_cursorRow, _cursorCol);
                if (index >= 0 && index < TotalSize)
                {
                    _cells[index] = isAttribute ? ' ' : c;
                    _attributes[index] = attribute;
                    _isAttribute[index] = isAttribute;
                }

                int next = (index + 1) % TotalSize;
                FromIndex(next, out _cursorRow, out _cursorCol);
            }
        }

        /// <summary>
        /// Writes an ASCII string starting at the current cursor position, advancing the cursor.
        /// </summary>
        public void WriteString(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                foreach (char c in text)
                {
                    WriteCharAndAdvance(c);
                }
            }
        }

        /// <summary>
        /// Writes an ASCII string starting at specified (row, col) coordinates.
        /// </summary>
        public void WriteAt(int row, int col, string text)
        {
            SetCursor(row, col);
            WriteString(text);
        }

        /// <summary>
        /// Implements 5250 Repeat to Address (RA) order.
        /// Fills characters from current cursor up to target coordinate.
        /// </summary>
        public void RepeatToAddress(int targetRow, int targetCol, char fillChar)
        {
            lock (_lock)
            {
                int startIndex = ToIndex(_cursorRow, _cursorCol);
                int targetIndex = ToIndex(Math.Max(1, Math.Min(Rows, targetRow)), Math.Max(1, Math.Min(Columns, targetCol)));

                if (targetIndex < startIndex)
                {
                    for (int i = startIndex; i < TotalSize; i++) _cells[i] = fillChar;
                    for (int i = 0; i <= targetIndex; i++) _cells[i] = fillChar;
                }
                else
                {
                    for (int i = startIndex; i <= targetIndex; i++) _cells[i] = fillChar;
                }

                // If following a StartField, record field length
                if (_fields.Count > 0)
                {
                    var lastField = _fields[_fields.Count - 1];
                    if (lastField.Length == 0)
                    {
                        int fStart = ToIndex(lastField.StartRow, lastField.StartCol);
                        if (targetIndex >= fStart)
                        {
                            lastField.Length = (targetIndex - fStart) + 1;
                        }
                    }
                }

                int next = (targetIndex + 1) % TotalSize;
                FromIndex(next, out _cursorRow, out _cursorCol);
            }
        }

        /// <summary>
        /// Records a 5250 Start Field order and updates previous field length.
        /// </summary>
        public void AddField(int row, int col, byte ffw1, byte ffw2, byte attributeByte)
        {
            lock (_lock)
            {
                int index = ToIndex(row, col);
                if (index >= 0 && index < TotalSize)
                {
                    _isAttribute[index] = true;
                    _attributes[index] = attributeByte;
                    _cells[index] = ' ';
                }

                // If previous field length not set, calculate it
                if (_fields.Count > 0)
                {
                    var prev = _fields[_fields.Count - 1];
                    if (prev.Length == 0)
                    {
                        int prevIndex = ToIndex(prev.StartRow, prev.StartCol);
                        int len = index - prevIndex;
                        if (len < 0) len += TotalSize;
                        prev.Length = len;
                    }
                }

                // Data starts at col + 1
                int dataCol = col + 1;
                int dataRow = row;
                if (dataCol > Columns)
                {
                    dataCol = 1;
                    dataRow = (row % Rows) + 1;
                }

                var newField = new Tn5250Field(dataRow, dataCol, ffw1, ffw2, attributeByte);
                _fields.Add(newField);
            }
        }

        /// <summary>
        /// Reads a contiguous linear slice of characters from the presentation space.
        /// </summary>
        public string ReadSlice(int startRow, int startCol, int length)
        {
            ValidateCoordinates(startRow, startCol);
            if (length <= 0) return string.Empty;

            lock (_lock)
            {
                int startIndex = ToIndex(startRow, startCol);
                int actualLength = Math.Min(length, TotalSize - startIndex);
                if (actualLength <= 0) return string.Empty;

                return new string(_cells, startIndex, actualLength);
            }
        }

        /// <summary>
        /// Reads a 2D rectangular box defined by (startRow, startCol) to (endRow, endCol).
        /// Lines are separated by Environment.NewLine.
        /// </summary>
        public string ReadBox(int startRow, int startCol, int endRow, int endCol)
        {
            ValidateCoordinates(startRow, startCol);
            if (endRow < startRow || endRow > Rows)
                throw new AS400Exception(AS400ErrorCode.InvalidCoordinate,
                    $"EndRow '{endRow}' is invalid. Must be between {startRow} and {Rows}.");
            if (endCol < startCol || endCol > Columns)
                throw new AS400Exception(AS400ErrorCode.InvalidCoordinate,
                    $"EndCol '{endCol}' is invalid. Must be between {startCol} and {Columns}.");

            lock (_lock)
            {
                StringBuilder sb = new StringBuilder();
                for (int r = startRow; r <= endRow; r++)
                {
                    int rowStart = ToIndex(r, startCol);
                    int width = (endCol - startCol) + 1;
                    sb.Append(new string(_cells, rowStart, width));
                    if (r < endRow)
                    {
                        sb.AppendLine();
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Reads until the end of the field or the next screen attribute byte starting from (row, col).
        /// </summary>
        public string ReadField(int row, int col)
        {
            ValidateCoordinates(row, col);

            lock (_lock)
            {
                int startIndex = ToIndex(row, col);
                int rowEnd = ToIndex(row, Columns);

                // Check if an explicit field definition exists covering this point
                foreach (var f in _fields)
                {
                    if (f.Contains(row, col))
                    {
                        int fStart = ToIndex(f.StartRow, f.StartCol);
                        int fEnd = Math.Min(fStart + f.Length, rowEnd + 1);
                        int remaining = fEnd - startIndex;
                        if (remaining > 0)
                        {
                            for (int i = startIndex + 1; i < fEnd; i++)
                            {
                                if (_isAttribute[i] || (_attributes[i] >= 0x20 && _attributes[i] <= 0x3F))
                                {
                                    remaining = i - startIndex;
                                    break;
                                }
                            }
                            return new string(_cells, startIndex, remaining).TrimEnd();
                        }
                    }
                }

                // Scan forward until next attribute cell, or next 5250 attribute (0x20..0x3F), or end of row
                int endIdx = startIndex;
                while (endIdx <= rowEnd && endIdx < TotalSize)
                {
                    if (endIdx > startIndex && (_isAttribute[endIdx] || (_attributes[endIdx] >= 0x20 && _attributes[endIdx] <= 0x3F)))
                    {
                        break;
                    }
                    endIdx++;
                }

                int len = endIdx - startIndex;
                if (len <= 0) return string.Empty;
                return new string(_cells, startIndex, len).TrimEnd();
            }
        }

        /// <summary>
        /// Returns the full 24x80 presentation space as a formatted multi-line string.
        /// </summary>
        public string GetFullPresentationSpace()
        {
            lock (_lock)
            {
                StringBuilder sb = new StringBuilder(TotalSize + (Rows * 2));
                for (int r = 1; r <= Rows; r++)
                {
                    int rowStart = ToIndex(r, 1);
                    sb.AppendLine(new string(_cells, rowStart, Columns));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Determines if the presentation space currently contains the specified text.
        /// </summary>
        public bool ContainsText(string targetText, bool ignoreCase = true)
        {
            if (string.IsNullOrEmpty(targetText)) return false;

            lock (_lock)
            {
                string fullScreen = new string(_cells);
                StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return fullScreen.IndexOf(targetText, comparison) >= 0;
            }
        }
    }
}
