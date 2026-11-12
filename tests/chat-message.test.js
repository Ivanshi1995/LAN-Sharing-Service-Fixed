const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('聊天窗口合并历史和实时消息，并保留同一时刻的独立消息', async () => {
    const page = fs.readFileSync(path.join(__dirname, '..', 'lan-file-share-client.html'), 'utf8');
    const mergeStart = page.indexOf('function _chatMergeMessages(');
    const mergeEnd = page.indexOf('// ========== 聊天历史加载状态', mergeStart);
    const historyStart = page.indexOf('async function _loadChatHistoryForWindow(');
    const historyEnd = page.indexOf('// 关闭聊天窗口', historyStart);
    assert.ok(mergeStart >= 0 && mergeEnd > mergeStart && historyStart >= 0 && historyEnd > historyStart);

    const partner = 'peer';
    const first = { id: 7, time: 1700000000000, from: partner, type: 'text', content: '同文' };
    const second = { ...first, id: 8 };
    const state = { visible: true, targetUsername: partner, chatMessages: {} };
    const context = vm.createContext({
        window: { _chatState: state, _chatHistoryLoadState: {} },
        _chatContent: { scrollHeight: 200, scrollTop: 0 },
        _chatRenderMessages() {},
        URLSearchParams,
        async fetch(url) {
            assert.equal(url, '/api/chat/history?with=peer&limit=50');
            return Response.json({ ok: true, messages: [second, first] });
        }
    });
    vm.runInContext(page.slice(mergeStart, mergeEnd) + page.slice(historyStart, historyEnd), context);

    const loading = context._loadChatHistoryForWindow(partner);
    assert.equal(context._chatAddMessage(partner, first), true);
    await loading;
    await context._loadChatHistoryForWindow(partner);
    assert.equal(context._chatAddMessage(partner, second), false);
    assert.deepEqual(Array.from(state.chatMessages[partner], message => message.id), [7, 8]);
});
