import { writeFileSync } from 'node:fs';
import { expect, it } from 'vitest';
import { protobuf_decode, protobuf_encode } from '@snowluma/proton';
import type { Elem, MsgInfo } from '@snowluma/proto-defs/element';
import type { MessageBody } from '@snowluma/proto-defs/message';
import { decodeRichBody } from '../../src/msg-push/rich-body-decoder';

function nt(fileName = 'image.png', isGroup = true, withUrl = true, withUuid = false,
    domain = 'multimedia.nt.qq.com.cn'): Elem {
    const info: MsgInfo = {
        msgInfoBody: [{
            index: {
                info: {
                    fileSize: 10711,
                    fileHash: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    fileSha1: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    fileName,
                    width: 1080,
                    height: 1080,
                    type: { picFormat: 1001 },
                },
                ...(withUuid ? { fileUuid: 'ORACLE-FILE-ID' } : {}),
            },
            ...(withUrl ? { picture: {
                domain,
                urlPath: `/download?appid=${isGroup ? 1407 : 1406}&fileid=ORACLE-FILE-ID`,
                ext: { originalParameter: '&rkey=ORACLE-FAKE-KEY' },
            } } : {}),
        }],
    };
    return { commonElem: {
        serviceType: 48,
        businessType: isGroup ? 20 : 10,
        pbElem: protobuf_encode<MsgInfo>(info),
    } };
}

function legacy(filePath = 'image.png', origUrl = '/gchatpic_new/0/legacy/0'): Elem {
    return { customFace: {
        filePath, origUrl,
        md5: Uint8Array.from({ length: 16 }, (_, index) => index + 1),
        size: 10711,
    } };
}

it('exports official-encoder wire inputs and official-decoder image results', () => {
    const cases: { name: string; isGroup: boolean; elems: Elem[] }[] = [
        { name: 'group-nt-no-uuid', isGroup: true, elems: [nt()] },
        { name: 'private-nt-no-uuid', isGroup: false, elems: [nt('private.png', false)] },
        { name: 'group-file-uuid-fallback', isGroup: true, elems: [nt('image.png', true, false, true)] },
        { name: 'group-md5-fallback', isGroup: true, elems: [nt('image.png', true, false)] },
        { name: 'legacy-group-image', isGroup: true, elems: [legacy()] },
        { name: 'separate-compatibility-sibling', isGroup: true, elems: [nt(), legacy()] },
        { name: 'same-elem-legacy-and-nt', isGroup: true, elems: [{ ...nt(), ...legacy() }] },
        { name: 'same-elem-text-and-nt', isGroup: true, elems: [{ ...nt(), text: { str: 'caption' } }] },
        { name: 'same-elem-reply-and-nt', isGroup: true, elems: [{ ...nt(), srcMsg: { origSeqs: [42] } }] },
        { name: 'same-elem-empty-legacy-and-nt', isGroup: true, elems: [{ ...nt(), customFace: {} }] },
        { name: 'query-only-unrelated-legacy', isGroup: true, elems: [nt(), legacy('other.png', '&rkey=ORACLE-FAKE-KEY')] },
        { name: 'case-distinct-filenames', isGroup: true, elems: [nt('IMAGE.png'), legacy('image.png')] },
        { name: 'md5-derived-filename-sibling', isGroup: true, elems: [nt(''), legacy('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png')] },
        { name: 'group-nt-server-domain-gchat', isGroup: true, elems: [nt('image.png', true, true, false, 'gchat.qpic.cn')] },
        { name: 'legacy-absolute-gchat-https', isGroup: true, elems: [legacy('image.png', 'https://gchat.qpic.cn/download?appid=1407&fileid=ORACLE-FILE-ID&rkey=ORACLE-FAKE-KEY')] },
        { name: 'legacy-absolute-gchat-http', isGroup: true, elems: [legacy('image.png', 'http://gchat.qpic.cn/download?appid=1407&fileid=ORACLE-FILE-ID&rkey=ORACLE-FAKE-KEY')] },
    ];
    const fixtures = cases.map(({ name, isGroup, elems }) => {
        const body: MessageBody = { richText: { elems } };
        const bytes = protobuf_encode<MessageBody>(body);
        const decoded = protobuf_decode<MessageBody>(bytes);
        const images = decodeRichBody(decoded, isGroup).filter(element => element.type === 'image');
        return { name, isGroup, bodyBase64: Buffer.from(bytes).toString('base64'),
            images: images.map(image => ({ url: image.imageUrl ?? '', md5: (image.md5Hex ?? '').toLowerCase() })) };
    });
    expect(fixtures).toHaveLength(16);
    writeFileSync(process.env.QQLISTENER_ORACLE_OUTPUT!, JSON.stringify({
        upstream: 'SnowLuma/SnowLuma',
        commit: 'f8cb15c27bca3d2d1bd044a921e55bfc44bb5e60',
        fixtures,
    }, null, 2) + '\n');
});
