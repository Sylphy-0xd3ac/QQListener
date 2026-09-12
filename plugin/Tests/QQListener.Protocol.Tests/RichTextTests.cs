namespace QQListener.Protocol.Tests;

/// <summary>
/// 移植自 Python 版 tests/native/test_message_media.py + test_message.py。
/// 图片地址、@、引用这三块是协议层最容易回归的地方，字节向量逐字保留。
/// </summary>
public class RichTextTests
{
    // ---------- 图片地址 ----------

    [Fact]
    public void NtImage_CarriesEmbeddedRkey()
    {
        // 缺 rkey 时 CDN 会以 invalid rkey 拒绝，图片就"炸"成占位符了。
        var segs = RichText.ParseElems(Pb.Rich(Pb.NtImageElem(rkey: "&rkey=CAESQabc")), isGroup: true);

        var seg = Assert.Single(segs);
        Assert.Equal("image", seg.Type);
        Assert.Equal(
            "https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=FID&spec=0&rkey=CAESQabc",
            seg.Url);
    }

    [Fact]
    public void MaliciousDomainInPictureElement_FallsBackToDefaultHostInsteadOfBeingUsedVerbatim()
    {
        // domain 来自消息内容本身，理论上可以被发消息的一方影响；不能允许它未经
        // 校验地被拼进最终会被真实请求的 URL 里。
        var segs = RichText.ParseElems(
            Pb.Rich(Pb.NtImageElem(domain: "evil.example.com@attacker.net")), isGroup: true);

        var image = Assert.Single(segs);
        Assert.DoesNotContain("attacker.net", image.Url);
        Assert.StartsWith("https://multimedia.nt.qq.com.cn/", image.Url); // 退回官方默认下载地址
    }

    [Fact]
    public void ControlCharacterInPicturePath_IsRejectedRatherThanEmbeddedInTheUrl()
    {
        var segs = RichText.ParseElems(
            Pb.Rich(Pb.NtImageElem(urlPath: "/download\r\nHost: evil")), isGroup: true);

        var image = Assert.Single(segs);
        Assert.DoesNotContain('\r', image.Url);
        Assert.DoesNotContain('\n', image.Url);
    }

    [Fact]
    public void NtImage_WithoutRkey_LeftBareForLaterSigning()
    {
        var segs = RichText.ParseElems(Pb.Rich(Pb.NtImageElem()), isGroup: true);

        Assert.EndsWith("&spec=0", segs[0].Url);
        Assert.DoesNotContain("rkey=", segs[0].Url);
    }

    [Theory]
    [InlineData("https://x/download?a=1", "&rkey=K", "https://x/download?a=1&rkey=K")]
    [InlineData("https://x/download", "?rkey=K", "https://x/download?rkey=K")]
    [InlineData("https://x/download?rkey=OLD", "K", "https://x/download?rkey=OLD")]
    [InlineData("", "K", "")]
    public void AppendRkey_StripsPrefixAndPicksSeparator(string url, string rkey, string expected) =>
        Assert.Equal(expected, RichText.AppendRkey(url, rkey));

    [Theory]
    [InlineData("/gchatpic_new/0/0-0-AB/0", "http://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0")]
    [InlineData("/download?fileid=x", "https://multimedia.nt.qq.com.cn/download?fileid=x")]
    [InlineData("https://already/absolute", "https://already/absolute")]
    [InlineData("?spec=0", "")]
    [InlineData("", "")]
    public void RelativeLegacyImagePath_GetsAHost(string input, string expected) =>
        Assert.Equal(expected, Elements.MakeImageUrl(input));

    [Fact]
    public void CustomFace_RelativeUrlIsCompleted()
    {
        var customFace = Pb.LenField(RichText.ElemCustomFace,
            Pb.LenField(16, "/gchatpic_new/0/0-0-AB/0"));

        var segs = RichText.ParseElems(Pb.Rich(customFace), isGroup: true);

        Assert.Equal("http://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0", segs[0].Url);
    }

    [Fact]
    public void ImageWithoutAnyUrl_FallsBackToMd5Address()
    {
        var md5 = string.Concat(Enumerable.Repeat("AB", 16));
        var customFace = Pb.LenField(RichText.ElemCustomFace,
            Pb.LenField(13, Pb.FromHex(md5)));

        var segs = RichText.ParseElems(Pb.Rich(customFace), isGroup: true);

        Assert.EndsWith("/gchatpic_new/0/0-0-ABABABABABABABABABABABABABABABAB/0", segs[0].Url);
    }

    [Fact]
    public void LegacySiblingOfSameNtImage_IsDropped()
    {
        // NT 图片会附带一份给老客户端的 CustomFace；QQ 只显示一张。
        const string md5 = "ea0cd2ae3bf8596f4d75481bd5b544b8";
        var sibling = Pb.LenField(RichText.ElemCustomFace, Pb.Cat(
            Pb.LenField(16, "/gchatpic_new/0/0-0-X/0"),
            Pb.LenField(13, Pb.FromHex(md5))));

        var segs = RichText.ParseElems(
            Pb.Rich(Pb.NtImageElem(md5: md5, rkey: "&rkey=K"), sibling), isGroup: true);

        var seg = Assert.Single(segs);
        Assert.Contains("multimedia.nt.qq.com.cn", seg.Url);
    }

    [Fact]
    public void UnrelatedLegacyImage_Survives()
    {
        var other = Pb.LenField(RichText.ElemCustomFace, Pb.Cat(
            Pb.LenField(16, "/gchatpic_new/0/0-0-Y/0"),
            Pb.LenField(13, Pb.FromHex(string.Concat(Enumerable.Repeat("BB", 16))))));

        var segs = RichText.ParseElems(
            Pb.Rich(Pb.NtImageElem(rkey: "&rkey=K"), other), isGroup: true);

        Assert.Equal(2, segs.Count);
    }

    // ---------- @ ----------

    [Fact]
    public void Mention_DecodedFromPbReserve()
    {
        var elem = Pb.TextElem("@李四 ", pbReserve: Pb.MentionExtra(1, uin: 1002));

        var segs = RichText.ParseElems(Pb.Rich(elem), isGroup: true);

        var seg = Assert.Single(segs);
        Assert.Equal("at", seg.Type);
        Assert.Equal("1002", seg.TargetId);
        Assert.Equal("@李四 ", seg.Text);
    }

    [Fact]
    public void MentionAll_IsFlaggedAsAll()
    {
        var elem = Pb.TextElem("@全体成员", pbReserve: Pb.MentionExtra(1, uin: 0));

        var segs = RichText.ParseElems(Pb.Rich(elem), isGroup: true);

        Assert.Equal("all", segs[0].TargetId);
    }

    [Fact]
    public void Mention_FromAttr6Buffer()
    {
        // attr6Buf[7..11] 是大端 uin
        byte[] attr6 = [.. new byte[7], 0, 0, 0x04, 0xD2, 0, 0];  // 1234
        var segs = RichText.ParseElems(Pb.Rich(Pb.TextElem("@王五 ", attr6: attr6)), isGroup: true);

        Assert.Equal("at", segs[0].Type);
        Assert.Equal("1234", segs[0].TargetId);
    }

    // ---------- 引用 ----------

    [Fact]
    public void Reply_CarriesQuotedMessageElements()
    {
        var quotedText = Pb.TextElem("昨天的作业交了吗");
        var quotedImage = Pb.NtImageElem(rkey: "&rkey=K", fileUuid: "QUOTED");
        var reply = Pb.SrcMsgElem(42, 1002, [quotedText, quotedImage], time: 1700000000);

        var segs = RichText.ParseElems(Pb.Rich(reply, Pb.TextElem("交了")), isGroup: true);

        Assert.Equal(["reply", "text"], segs.Select(s => s.Type));
        Assert.Equal(42UL, segs[0].Get<ulong>("seq"));
        Assert.Equal("1002", segs[0].Get<string>("sender_id"));
        Assert.Equal(1700000000, segs[0].Get<int>("time"));

        var quoted = segs[0].Get<List<Segment>>("segments")!;
        Assert.Equal(["text", "image"], quoted.Select(s => s.Type));
        Assert.Equal("昨天的作业交了吗", quoted[0].Text);
        Assert.Contains("rkey=K", quoted[1].Url);
        Assert.Equal("交了", segs[1].Text);
    }

    [Fact]
    public void Reply_StructuralAutoMentionAndBlankSeparatorAreDropped()
    {
        // QQ NT 在 srcMsg 后塞一个 type=2/uin=0 的结构性 @ 和一个空白文本
        var reply = Pb.SrcMsgElem(42, 1002, [Pb.TextElem("原文")]);
        var autoMention = Pb.TextElem("@李四 ", pbReserve: Pb.MentionExtra(2, uin: 0, uid: "u_a"));
        var blank = Pb.TextElem(" ");

        var segs = RichText.ParseElems(
            Pb.Rich(reply, autoMention, blank, Pb.TextElem("交了")), isGroup: true);

        Assert.Equal(["reply", "text"], segs.Select(s => s.Type));
        Assert.Equal("交了", segs[1].Text);
    }

    [Fact]
    public void RealUserMentionAfterReply_IsKept()
    {
        var reply = Pb.SrcMsgElem(42, 1002, [Pb.TextElem("原文")]);
        var realAt = Pb.TextElem("@张三 ", pbReserve: Pb.MentionExtra(1, uin: 1001));

        var segs = RichText.ParseElems(
            Pb.Rich(reply, realAt, Pb.TextElem("看看")), isGroup: true);

        Assert.Equal(["reply", "at", "text"], segs.Select(s => s.Type));
        Assert.Equal("1001", segs[1].TargetId);
    }

    /// <summary>回归测试：ConvertElems/ReplySegment 原本是无界互递归，一条嵌套够深的
    /// 引用链（SrcMsg 里嵌 SrcMsg）会栈溢出——StackOverflowException 在 .NET 里
    /// 不可捕获，会直接杀死整个宿主进程。50 层远超实际防护上限（8），只用来证明
    /// 深度限制真的生效、不会崩溃。</summary>
    [Fact]
    public void DeeplyNestedReplyChainDoesNotStackOverflow()
    {
        var nested = Pb.TextElem("最内层");
        for (var i = 0; i < 50; i++)
            nested = Pb.SrcMsgElem((ulong)i, 1, [nested]);

        var segs = RichText.ParseElems(Pb.Rich(nested), isGroup: true);

        Assert.Equal(["reply"], segs.Select(s => s.Type));
    }

    // ---------- 其他元素 ----------

    [Fact]
    public void TextThenImage()
    {
        var textElem = Pb.LenField(RichText.ElemText, Pb.LenField(1, "hi"));
        var imageElem = Pb.LenField(RichText.ElemNotOnlineImage, Pb.LenField(15, "http://orig"));

        var segs = RichText.ParseElems(Pb.Rich(textElem, imageElem));

        Assert.Equal(["text", "image"], segs.Select(s => s.Type));
        Assert.Equal("hi", segs[0].Text);
        Assert.Equal("http://orig", segs[1].Url);
    }

    [Fact]
    public void GroupFileElem()
    {
        var inner = Pb.Cat(
            Pb.LenField(1, "作业.docx"),
            Pb.VarintField(2, 2048),
            Pb.LenField(3, "fid"));

        var segs = RichText.ParseElems(Pb.Rich(Pb.LenField(RichText.ElemGroupFile, inner)));

        var seg = Assert.Single(segs);
        Assert.Equal("file", seg.Type);
        Assert.Equal("作业.docx", seg.Name);
        Assert.Equal(2048UL, seg.Get<ulong>("size"));
        Assert.Equal("fid", seg.Get<string>("file_id"));
    }

    [Fact]
    public void NtGroupFileTransElem()
    {
        var info = Pb.Cat(
            Pb.VarintField(1, 102),
            Pb.LenField(2, "group-file-id"),
            Pb.VarintField(3, 4096),
            Pb.LenField(4, "群资料.pdf"));
        var extra = Pb.Cat(Pb.VarintField(1, 6), Pb.LenField(7, Pb.LenField(2, info)));
        byte[] value = [0x01, (byte)(extra.Length >> 8), (byte)(extra.Length & 0xFF), .. extra];
        var trans = Pb.Cat(Pb.VarintField(1, 24), Pb.LenField(2, value));

        var segs = RichText.ParseElems(
            Pb.Rich(Pb.LenField(RichText.ElemTrans, trans)), isGroup: true);

        var seg = Assert.Single(segs);
        Assert.Equal("file", seg.Type);
        Assert.Equal("群资料.pdf", seg.Name);
        Assert.Equal("group-file-id", seg.Get<string>("file_id"));
        Assert.Equal(4096UL, seg.Get<ulong>("size"));
    }

    [Fact]
    public void UnknownElement_IsSkippedNotFatal()
    {
        // 未知字段号不应该让整条消息解不出来
        var unknown = Pb.LenField(99, Pb.LenField(1, "?"));
        var segs = RichText.ParseElems(Pb.Rich(unknown, Pb.TextElem("正文")));

        var seg = Assert.Single(segs);
        Assert.Equal("正文", seg.Text);
    }
}
