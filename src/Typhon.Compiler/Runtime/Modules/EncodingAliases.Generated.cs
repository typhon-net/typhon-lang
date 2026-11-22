// Derived from CPython 3.15.2 Lib/encodings/aliases.py.
// See CPython-LICENSE.txt for the complete upstream license and copyright notices.
// Regenerate with GenerateEncodingAliases.py using the specified CPython version.

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;

    internal static class EncodingAliasData
    {
        internal static OrderedDictionary<string, string> Load()
        {
            const string rows = """
                ascii 646 ansi_x3.4_1968 ansi_x3_4_1968 ansi_x3.4_1986 cp367 csascii ibm367 iso646_us iso_646.irv_1991 iso_ir_6 us us_ascii
                base64_codec base64 base_64
                big5 big5_tw csbig5
                big5hkscs big5_hkscs csbig5hkscs hkscs
                bz2_codec bz2
                cp037 037 csibm037 ebcdic_cp_ca ebcdic_cp_nl ebcdic_cp_us ebcdic_cp_wt ibm037 ibm039
                cp1026 1026 csibm1026 ibm1026
                cp1125 1125 ibm1125 cp866u ruscii
                cp1140 1140 ccsid01140 cp01140 csibm01140 ebcdic_us_37_euro ibm01140 ibm1140
                cp1250 1250 cswindows1250 windows_1250
                cp1251 1251 cswindows1251 windows_1251
                cp1252 1252 cswindows1252 windows_1252
                cp1253 1253 cswindows1253 windows_1253
                cp1254 1254 cswindows1254 windows_1254
                cp1255 1255 cswindows1255 windows_1255
                cp1256 1256 cswindows1256 windows_1256
                cp1257 1257 cswindows1257 windows_1257
                cp1258 1258 cswindows1258 windows_1258
                cp273 273 ibm273 csibm273
                cp424 424 csibm424 ebcdic_cp_he ibm424
                cp437 437 cspc8codepage437 ibm437
                cp500 500 csibm500 ebcdic_cp_be ebcdic_cp_ch ibm500
                cp775 775 cspc775baltic ibm775
                cp850 850 cspc850multilingual ibm850
                cp852 852 cspcp852 ibm852
                cp855 855 csibm855 ibm855
                cp857 857 csibm857 ibm857
                cp858 858 ccsid00858 cp00858 csibm00858 csibm858 ibm00858 ibm858 pc_multilingual_850_euro
                cp860 860 csibm860 ibm860
                cp861 861 cp_is csibm861 ibm861
                cp862 862 cspc862latinhebrew ibm862
                cp863 863 csibm863 ibm863
                cp864 864 csibm864 ibm864
                cp865 865 csibm865 ibm865
                cp866 866 csibm866 ibm866
                cp869 869 cp_gr csibm869 ibm869
                cp874 874 cswindows874 ms874 windows_874
                cp932 932 cswindows31j ms932 mskanji ms_kanji windows_31j
                cp949 949 csksc56011987 iso_ir_149 korean ks_c_5601_1987 ks_c_5601_1989 ksc5601 ks_c_5601 ksc_5601 ksx1001 ks_x_1001 ms949 uhc
                cp950 950 ms950
                euc_jis_2004 jisx0213 eucjis2004 euc_jis2004
                euc_jisx0213 eucjisx0213
                euc_jp cseucpkdfmtjapanese eucjp ujis u_jis
                euc_kr cseuckr euckr
                gb18030 csgb18030 gb18030_2000
                gb2312 chinese csgb2312 csiso58gb231280 euc_cn euccn eucgb2312_cn gb2312_1980 gb2312_80 gb_2312_80 iso_ir_58
                gbk 936 cp936 csgbk ms936 windows_936
                hex_codec hex
                hp_roman8 cp1051 cshproman8 ibm1051 r8 roman8
                hz hzgb hz_gb hz_gb_2312
                iso2022_jp csiso2022jp iso2022jp iso_2022_jp
                iso2022_jp_1 iso2022jp_1 iso_2022_jp_1
                iso2022_jp_2 csiso2022jp2 iso2022jp_2 iso_2022_jp_2
                iso2022_jp_2004 iso_2022_jp_2004 iso2022jp_2004
                iso2022_jp_3 iso2022jp_3 iso_2022_jp_3
                iso2022_jp_ext iso2022jp_ext iso_2022_jp_ext
                iso2022_kr csiso2022kr iso2022kr iso_2022_kr
                iso8859_10 csisolatin6 iso_8859_10 iso_8859_10_1992 iso_ir_157 l6 latin6 latin_6
                iso8859_11 thai iso_8859_11 iso_8859_11_2001
                iso8859_13 csiso885913 iso_8859_13 l7 latin7 latin_7
                iso8859_14 csiso885914 iso_8859_14 iso_8859_14_1998 iso_celtic iso_ir_199 l8 latin8 latin_8
                iso8859_15 csiso885915 iso_8859_15 l9 latin9 latin_9
                iso8859_16 csiso885916 iso_8859_16 iso_8859_16_2001 iso_ir_226 l10 latin10 latin_10
                iso8859_2 csisolatin2 iso_8859_2 iso_8859_2_1987 iso_ir_101 l2 latin2 latin_2
                iso8859_3 csisolatin3 iso_8859_3 iso_8859_3_1988 iso_ir_109 l3 latin3 latin_3
                iso8859_4 csisolatin4 iso_8859_4 iso_8859_4_1988 iso_ir_110 l4 latin4 latin_4
                iso8859_5 csisolatincyrillic cyrillic iso_8859_5 iso_8859_5_1988 iso_ir_144
                iso8859_6 arabic asmo_708 csisolatinarabic ecma_114 iso_8859_6 iso_8859_6_1987 iso_ir_127
                iso8859_7 csisolatingreek ecma_118 elot_928 greek greek8 iso_8859_7 iso_8859_7_1987 iso_ir_126
                iso8859_8 csiso88598e csiso88598i csisolatinhebrew hebrew iso_8859_8 iso_8859_8_1988 iso_ir_138 iso_8859_8_i iso_8859_8_e
                iso8859_9 csisolatin5 iso_8859_9 iso_8859_9_1989 iso_ir_148 l5 latin5 latin_5
                johab cp1361 ms1361
                koi8_r cskoi8r
                koi8_u cskoi8u
                kz1048 cskz1048 kz_1048 rk1048 strk1048_2002
                latin_1 8859 cp819 csisolatin1 ibm819 iso8859 iso8859_1 iso_8859_1 iso_8859_1_1987 iso_ir_100 l1 latin latin1
                mac_cyrillic maccyrillic
                mac_greek macgreek
                mac_iceland maciceland
                mac_latin2 maccentraleurope mac_centeuro maclatin2
                mac_roman csmacintosh macintosh mac macroman
                mac_turkish macturkish
                mbcs ansi dbcs
                ptcp154 csptcp154 pt154 cp154 cyrillic_asian
                quopri_codec quopri quoted_printable quotedprintable
                rot_13 rot13
                shift_jis csshiftjis shiftjis sjis s_jis
                shift_jis_2004 shiftjis2004 sjis_2004 s_jis_2004
                shift_jisx0213 shiftjisx0213 sjisx0213 s_jisx0213
                tis_620 cstis620 tis620 tis_620_0 tis_620_2529_0 tis_620_2529_1 iso_ir_166
                utf_16 csutf16 u16 utf16
                utf_16_be csutf16be unicodebigunmarked utf_16be
                utf_16_le csutf16le unicodelittleunmarked utf_16le
                utf_32 csutf32 u32 utf32
                utf_32_be csutf32be utf_32be
                utf_32_le csutf32le utf_32le
                utf_7 csunicode11utf7 csutf7 u7 utf7 unicode_1_1_utf_7
                utf_8 csutf8 u8 utf utf8 utf8_ucs2 utf8_ucs4 cp65001
                utf_8_sig utf8_sig
                uu_codec uu
                zlib_codec zip zlib
                shift_jis x_mac_japanese
                euc_kr x_mac_korean
                gb2312 x_mac_simp_chinese
                big5 x_mac_trad_chinese
                """;
            var aliases = new OrderedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string row in rows.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] names = row.Trim().Split(' ');
                for (int i = 1; i < names.Length; i++) aliases.Add(names[i], names[0]);
            }
            return aliases;
        }
    }
}
