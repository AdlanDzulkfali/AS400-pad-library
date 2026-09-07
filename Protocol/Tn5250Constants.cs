namespace AS400Automation.Protocol
{
    /// <summary>
    /// Constants and protocol definitions for RFC 854 / RFC 1205 / RFC 2877 Telnet and IBM 5250 Data Streams.
    /// </summary>
    public static class Tn5250Constants
    {
        #region Standard Telnet Commands (RFC 854)
        public const byte IAC  = 255; // 0xFF - Interpret As Command
        public const byte DONT = 254; // 0xFE
        public const byte DO   = 253; // 0xFD
        public const byte WONT = 252; // 0xFC
        public const byte WILL = 251; // 0xFB
        public const byte SB   = 250; // 0xFA - Subnegotiation Begin
        public const byte GA   = 249; // 0xF9 - Go Ahead
        public const byte EL   = 248; // 0xF8 - Erase Line
        public const byte EC   = 247; // 0xF7 - Erase Character
        public const byte AYT  = 246; // 0xF6 - Are You There
        public const byte AO   = 245; // 0xF5 - Abort Output
        public const byte IP   = 244; // 0xF4 - Interrupt Process
        public const byte SE   = 240; // 0xF0 - Subnegotiation End
        public const byte EOR  = 239; // 0xEF - End of Record (RFC 885)
        #endregion

        #region Telnet Options
        public const byte OPT_TRANSMIT_BINARY = 0;   // RFC 856
        public const byte OPT_ECHO            = 1;   // RFC 857
        public const byte OPT_SUPPRESS_GA     = 3;   // RFC 858
        public const byte OPT_TIMING_MARK     = 6;   // RFC 860
        public const byte OPT_TERMINAL_TYPE   = 24;  // RFC 1091
        public const byte OPT_END_OF_RECORD   = 25;  // RFC 885
        public const byte OPT_NEW_ENVIRON     = 39;  // RFC 1572
        #endregion

        #region Telnet Subnegotiation Qualifiers
        public const byte QUAL_IS   = 0;
        public const byte QUAL_SEND = 1;
        #endregion

        #region Terminal Model Identifiers
        public const string TERMINAL_MODEL_IBM3179 = "IBM-3179-2";
        public const string TERMINAL_MODEL_IBM5555 = "IBM-5555-C01";
        public const string TERMINAL_MODEL_IBM3477 = "IBM-3477-FC";
        public const string TERMINAL_MODEL_IBM5250 = "IBM-5250";
        #endregion

        #region Screen Geometry (Standard 24x80 & 27x132)
        public const int SCREEN_ROWS = 24;
        public const int SCREEN_COLS = 80;
        public const int SCREEN_SIZE = SCREEN_ROWS * SCREEN_COLS; // 1920

        public const int WIDE_SCREEN_ROWS = 27;
        public const int WIDE_SCREEN_COLS = 132;
        public const int WIDE_SCREEN_SIZE = WIDE_SCREEN_ROWS * WIDE_SCREEN_COLS; // 3564
        #endregion

        #region 5250 Workstation Control Commands (Escape sequence 0x04)
        public const byte ESC = 0x04;
        public const byte CMD_WRITE_TO_DISPLAY        = 0x11;
        public const byte CMD_RESTORE_SCREEN          = 0x12;
        public const byte CMD_SAVE_PARTIAL_SCREEN     = 0x16;
        public const byte CMD_CLEAR_UNIT_ALTERNATE    = 0x20;
        public const byte CMD_CLEAR_UNIT              = 0x40;
        public const byte CMD_CLEAR_FORMAT_TABLE      = 0x50;
        public const byte CMD_READ_INPUT_FIELDS       = 0x42;
        public const byte CMD_READ_MDT_FIELDS         = 0x52;
        public const byte CMD_READ_SCREEN             = 0x62;
        public const byte CMD_WRITE_STRUCTURED_FIELD  = 0xF3;
        #endregion

        #region 5250 Orders
        public const byte ORDER_SOH = 0x01; // Start of Header
        public const byte ORDER_RA  = 0x02; // Repeat to Address
        public const byte ORDER_EA  = 0x03; // Erase to Address
        public const byte ORDER_TD  = 0x10; // Transparent Data
        public const byte ORDER_SBA = 0x11; // Set Buffer Address
        public const byte ORDER_WEA = 0x12; // Write Extended Attribute
        public const byte ORDER_IC  = 0x13; // Insert Cursor
        public const byte ORDER_MC  = 0x14; // Move Cursor
        public const byte ORDER_SF  = 0x1D; // Start Field
        #endregion

        #region 5250 Attention Identification (AID) Codes
        public const byte AID_NO_AID           = 0x00;
        public const byte AID_ENTER            = 0xF1;
        public const byte AID_HELP             = 0xF3;
        public const byte AID_PAGE_UP          = 0xF4; // Roll Down
        public const byte AID_PAGE_DOWN        = 0xF5; // Roll Up
        public const byte AID_PRINT            = 0xF6;
        public const byte AID_RECORD_BACKSPACE = 0xF8;
        public const byte AID_CLEAR            = 0xBD;

        // Function Keys F1..F12
        public const byte AID_PF1  = 0x31;
        public const byte AID_PF2  = 0x32;
        public const byte AID_PF3  = 0x33;
        public const byte AID_PF4  = 0x34;
        public const byte AID_PF5  = 0x35;
        public const byte AID_PF6  = 0x36;
        public const byte AID_PF7  = 0x37;
        public const byte AID_PF8  = 0x38;
        public const byte AID_PF9  = 0x39;
        public const byte AID_PF10 = 0x3A;
        public const byte AID_PF11 = 0x3B;
        public const byte AID_PF12 = 0x3C;

        // Function Keys F13..F24
        public const byte AID_PF13 = 0xB1;
        public const byte AID_PF14 = 0xB2;
        public const byte AID_PF15 = 0xB3;
        public const byte AID_PF16 = 0xB4;
        public const byte AID_PF17 = 0xB5;
        public const byte AID_PF18 = 0xB6;
        public const byte AID_PF19 = 0xB7;
        public const byte AID_PF20 = 0xB8;
        public const byte AID_PF21 = 0xB9;
        public const byte AID_PF22 = 0xBA;
        public const byte AID_PF23 = 0xBB;
        public const byte AID_PF24 = 0xBC;
        #endregion
    }
}
