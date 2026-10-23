# SnowLuma 图片解码交叉对照

`snowluma-image-oracle.json` 不是由 QQListener 的 `Pb` 助手生成的。
输入字节由 SnowLuma 的 `protobuf_encode<MessageBody>` 生成，再由其
`protobuf_decode<MessageBody>` 和 `decodeRichBody` 生成预期图片 URL、MD5。
生成器是本目录的 `snowluma-image-oracle.test.ts`，包含本项目设计的 16 个对照场景，
不是 16 份真实 QQ 抓包，也不是声称这些场景来自官方测试。

上游固定为 [SnowLuma/SnowLuma 的 f8cb15c](https://github.com/SnowLuma/SnowLuma/tree/f8cb15c27bca3d2d1bd044a921e55bfc44bb5e60)。
生成器通过上游 Vite/Proton 插件执行，使用官方生成式 protobuf 编解码器而非手写替代品。
数据只包含虚构文件标识和 `ORACLE-FAKE-KEY`，不包含真实聊天、账号或下载凭证。

## 覆盖范围

- NT 群图、私聊图、无 fileUuid 的图片、fileUuid 和 MD5 下载地址回退。
- NT 与旧图片位于不同 Elem，或同时出现在同一个 Elem。
- 同一 Elem 同时有文字、引用或空旧图片字段时，NT 图片仍可解析。
- 只有查询串的旧图片地址不能伪造成可下载图片。
- 区分大小写的文件名、由 MD5 派生的文件名参与去重。
- NT 的 `picture.domain`、旧图片的完整 HTTP(S) URL 保留服务端域名。

首次对照：旧实现 6/13 通过、7/13 失败；扩展后额外发现 3 组域名改写不一致。
最终 16/16 通过。
这证明已覆盖输入上的图片 URL、MD5 与官方实现一致，不能证明真实 QQ 的下载凭证、
CDN 响应、位图解码或 Windows 宿主运行结果。

## 重新生成

设置 `PLUGIN_ROOT` 为本插件的绝对路径，在固定提交的上游检出中执行。
使用符合该提交 `package.json` 的 Node/pnpm 环境；安装时不执行依赖生命周期脚本。

```sh
pnpm --filter @snowluma/protocol... install --frozen-lockfile --ignore-scripts
pnpm --filter @snowluma/proton build
cp "$PLUGIN_ROOT/Tests/QQListener.Protocol.Tests/Fixtures/snowluma-image-oracle.test.ts" \
  packages/protocol/tests/msg-push/qqlistener-image-oracle.test.ts
QQLISTENER_ORACLE_OUTPUT="$PLUGIN_ROOT/Tests/QQListener.Protocol.Tests/Fixtures/snowluma-image-oracle.json" \
  pnpm --filter @snowluma/protocol exec vitest run tests/msg-push/qqlistener-image-oracle.test.ts
```

插件测试只读取已生成的 JSON，不安装或执行 JavaScript 依赖。
