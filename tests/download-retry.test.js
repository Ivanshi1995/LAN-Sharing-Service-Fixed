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

test('公共共享重新下载时忽略旧进度，并只保存完整文件', async () => {
    const page = fs.readFileSync(path.join(__dirname, '..', 'lan-file-share-public.html'), 'utf8');
    const start = page.indexOf('// ========== 公共页下载管理');
    const end = page.indexOf('\n// Upload', start);
    const fileName = '公共共享.bin';
    const filePath = '__public__/0/' + fileName;
    const fullContent = Buffer.from('公共共享测试文件的完整内容');
    const prefix = fullContent.subarray(0, 9);
    const legacyProgress = JSON.stringify({ [filePath]: { received: prefix.length, total: fullContent.length } });
    const elements = new Map();
    const savedFiles = [];
    const requests = [];
    const headers = { 'Content-Length': String(fullContent.length) };
    let chunkSent = false;
    const interrupted = new ReadableStream({
        pull(controller) {
            if (chunkSent) {
                controller.error(new TypeError('连接中断'));
                return;
            }
            controller.enqueue(prefix);
            chunkSent = true;
        }
    }, { highWaterMark: 0 });
    const responses = [
        new Response(interrupted, { headers }),
        new Response(fullContent, { headers }),
        new Response(fullContent.subarray(prefix.length), { status: 206 }),
        new Response(prefix, { headers })
    ];
    const context = vm.createContext({
        AbortController, Blob,
        localStorage: { getItem: () => legacyProgress, setItem() {} },
        URL: {
            createObjectURL(blob) { savedFiles.push(blob); return 'blob:test'; },
            revokeObjectURL() {}
        },
        document: {
            getElementById(id) {
                if (!elements.has(id)) elements.set(id, { style: {}, classList: { add() {}, remove() {} } });
                return elements.get(id);
            },
            createElement() { return { click() {} }; },
            body: { appendChild() {}, removeChild() {} }
        },
        logAction() {}, formatFileSize: String, setTimeout() {}, clearTimeout() {},
        async fetch(requestUrl, options) {
            requests.push({ requestUrl, range: options.headers?.Range });
            return responses.shift();
        }
    });
    assert.match(page, /onclick="pubRetryDownload\(\)"[^>]*>重新下载<\/button>/);
    vm.runInContext(page.slice(start, end), context);
    await context.downloadPublicFile(filePath, fileName);
    assert.equal(chunkSent, true);
    assert.equal(elements.get('pub-btn-dl-retry').style.display, '');
    assert.equal(savedFiles.length, 0);

    await context.pubRetryDownload();
    assert.equal(savedFiles.length, 1);
    assert.deepEqual(Buffer.from(await savedFiles[0].arrayBuffer()), fullContent);

    await context.downloadPublicFile(filePath, fileName);
    assert.equal(elements.get('pub-btn-dl-retry').style.display, '');
    assert.equal(savedFiles.length, 1, '不能保存 HTTP 206 的文件片段');

    await context.pubRetryDownload();
    assert.equal(elements.get('pub-btn-dl-retry').style.display, '');
    assert.equal(savedFiles.length, 1, '不能保存字节数不足的文件');
    const requestUrl = '/api/public/files/download?path=' + encodeURIComponent(filePath);
    assert.deepEqual(requests, Array.from({ length: 4 }, () => ({ requestUrl, range: undefined })));
});
