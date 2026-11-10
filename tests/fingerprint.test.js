'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const vm = require('node:vm');
const Sha256 = require('../CDN/upload-fingerprint.js');
const source = fs.readFileSync(require.resolve('../CDN/upload-fingerprint.js'),'utf8');

for (const length of [0,1,55,56,63,64,65,127,128,100000,5242883]) {
    test('SHA-256 与标准实现一致，长度 '+length, () => {
        const bytes = crypto.randomBytes(length);
        const hash = new Sha256();
        for (let i=0;i<length;i+=97) hash.update(bytes.subarray(i,i+97));
        assert.equal(hash.digest(), crypto.createHash('sha256').update(bytes).digest('hex'));
    });
}
test('工作线程可读取多块文件并返回完整校验值', async () => {
    const bytes = crypto.randomBytes(5242883);
    let result;
    const self = {postMessage: data => {result=data;}};
    vm.runInNewContext(source,{self});
    await self.onmessage({data: new Blob([bytes])});
    assert.equal(result.hash,crypto.createHash('sha256').update(bytes).digest('hex'));
});
test('文件读取失败会返回错误', async () => {
    let result;
    const self={postMessage:data=>{result=data;}};
    vm.runInNewContext(source,{self});
    await self.onmessage({data:{size:1,slice:()=>{throw new Error('读取失败');}}});
    assert.equal(result.error,'读取失败');
});
test('用户取消校验时终止工作线程', async () => {
    let terminated=false;
    class Worker {
        postMessage() {}
        terminate() { terminated=true; }
    }
    const window={};
    vm.runInNewContext(source,{window,Worker,DOMException});
    const controller=new AbortController();
    const work=window.fingerprintUploadFile(new Blob(['abc']),controller.signal);
    controller.abort();
    await assert.rejects(work,{name:'AbortError'});
    assert.equal(terminated,true);
});
