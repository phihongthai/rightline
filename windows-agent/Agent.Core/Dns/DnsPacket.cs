using System.Text;

namespace KidSafe.Agent.Core;

/// <summary>Just enough DNS wire format to filter queries and forge sinkhole answers.</summary>
public static class DnsPacket
{
    public const ushort TypeA = 1, TypeAAAA = 28;

    /// <summary>Parses a standard single-question query. questionEnd = offset just after QCLASS.</summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> p, out string name, out ushort qtype, out int questionEnd)
    {
        name = ""; qtype = 0; questionEnd = 0;
        if (p.Length < 12 + 5) return false;
        if ((p[2] & 0x80) != 0) return false;                         // QR must be 0 (query)
        if (((p[2] >> 3) & 0x0F) != 0) return false;                  // opcode QUERY only
        if ((p[4] << 8 | p[5]) != 1) return false;                    // exactly one question

        var sb = new StringBuilder();
        int i = 12;
        while (true)
        {
            if (i >= p.Length) return false;
            int len = p[i++];
            if (len == 0) break;
            if ((len & 0xC0) != 0 || i + len > p.Length) return false; // no compression in questions
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(p.Slice(i, len)));
            i += len;
            if (sb.Length > 253) return false;
        }
        if (i + 4 > p.Length) return false;
        qtype = (ushort)(p[i] << 8 | p[i + 1]);
        questionEnd = i + 4;
        name = sb.ToString().ToLowerInvariant();
        return true;
    }

    /// <summary>Sinkhole: A -> 0.0.0.0, AAAA -> ::, anything else -> NXDOMAIN.</summary>
    public static byte[] BuildBlocked(byte[] query, int questionEnd, ushort qtype)
    {
        bool answer = qtype is TypeA or TypeAAAA;
        int rdLen = qtype == TypeAAAA ? 16 : 4;
        var r = new byte[questionEnd + (answer ? 12 + rdLen : 0)];
        Header(query, r, rcode: answer ? 0 : 3, ancount: answer ? 1 : 0);
        Array.Copy(query, 12, r, 12, questionEnd - 12);
        if (answer)
        {
            int o = questionEnd;
            r[o++] = 0xC0; r[o++] = 0x0C;                                    // name pointer to question
            r[o++] = (byte)(qtype >> 8); r[o++] = (byte)qtype;               // TYPE
            r[o++] = 0; r[o++] = 1;                                          // CLASS IN
            r[o++] = 0; r[o++] = 0; r[o++] = 0; r[o++] = 60;                 // TTL 60
            r[o++] = 0; r[o++] = (byte)rdLen;                                // RDLENGTH, RDATA stays zero
        }
        return r;
    }

    public static byte[] BuildServFail(byte[] query, int questionEnd)
    {
        var r = new byte[questionEnd];
        Header(query, r, rcode: 2, ancount: 0);
        Array.Copy(query, 12, r, 12, questionEnd - 12);
        return r;
    }

    private static void Header(byte[] q, byte[] r, int rcode, int ancount)
    {
        r[0] = q[0]; r[1] = q[1];                                            // ID
        r[2] = (byte)(0x80 | (q[2] & 0x79));                                 // QR=1, keep opcode+RD, AA/TC cleared
        r[3] = (byte)(0x80 | rcode);                                         // RA=1
        r[4] = 0; r[5] = 1;                                                  // QDCOUNT
        r[6] = (byte)(ancount >> 8); r[7] = (byte)ancount;
    }
}
