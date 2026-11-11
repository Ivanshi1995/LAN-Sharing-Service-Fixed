const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('中断后的下载必须生成完整文件，包括升级前保存的进度', async () => {
    const page = fs.readFileSync(path.join(__dirname, '..', 'lan-file-share-client.html'), 'utf8');
    const start = page.indexOf('async function downloadFile(');
    const end = page.indexOf('\nfunction cancelDownload()', start);
    const fullContent = Buffer.from('完整下载内容：前半段和后半段');
    const filePath = 'example.txt';
    const url = '/api/files/download?path=' + filePath;
    const states = { [filePath]: { url, received: 12, total: fullContent.length } };
    const elements = new Map();
    let generatedBlob;
    let sentRange;
    const context = vm.createContext({
        _dlAbortController: null, _dlChunks: [], fetchAuth: {},
        AbortController, Blob, Date,
        URL: {
            createObjectURL(blob) { generatedBlob = blob; return 'blob:test'; },
            revokeObjectURL() {}
        },
        document: {
            getElementById(id) {
                if (!elements.has(id)) elements.set(id, { style: {}, innerText: '' });
                return elements.get(id);
            },
            createElement() { return { click() {} }; },
            body: { appendChild() {}, removeChild() {} }
        },
        getDownloadStates: () => states,
        saveDownloadState(key, state) { states[key] = state; },
        showDownloadPanel() {}, hideDownloadPanel() {},
        updateDownloadProgress() {}, showToast() {}, logAction() {},
        formatSize: String, confirm: () => true, setTimeout() {},
        async fetch(requestUrl, options) {
            assert.equal(requestUrl, url);
            if (options.method === 'HEAD') {
                return new Response(null, { headers: { 'Content-Length': fullContent.length } });
            }
            sentRange = options.headers?.Range;
            const bytes = sentRange ? fullContent.subarray(12) : fullContent;
            return new Response(bytes, {
                status: sentRange ? 206 : 200,
                headers: { 'Content-Length': bytes.length, 'Content-Range': 'bytes 12-' + (fullContent.length - 1) + '/' + fullContent.length }
            });
        }
    });
    vm.runInContext(page.slice(start, end), context);
    await context.downloadFile(filePath, false);
    assert.equal(sentRange, undefined);
    assert.ok(generatedBlob, '下载必须生成文件');
    assert.deepEqual(Buffer.from(await generatedBlob.arrayBuffer()), fullContent);
    assert.equal(states[filePath], null);
});
