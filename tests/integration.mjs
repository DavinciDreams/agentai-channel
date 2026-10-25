import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { generateSecretKey, finalizeEvent } from 'nostr-tools/pure';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const local = !process.env.TEST_BASE_URL;
const directory = local ? mkdtempSync(join(tmpdir(), 'agentai-test-')) : null;
const root = resolve(import.meta.dirname, '..');
let server, output = '', checks = 0;
const suffix = Date.now().toString(36);
const alice = `test_a_${suffix}`, bob = `test_b_${suffix}`;
async function request(path, method = 'GET', body, token, expected = 200, headers = {}) {
  const res = await fetch(base + path, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}), ...headers }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000) });
  const text = await res.text();
  assert.equal(res.status, expected, `${method} ${path}: ${res.status} ${text}`); checks++;
  if (!text) return null;
  try { return JSON.parse(text); } catch { return text; }
}
async function start() {
  server = spawn('dotnet', [join(root, 'Chirper.Web/bin/Release/net10.0/Chirper.Web.dll'), '--urls', base], { cwd: join(root,'Chirper.Web'), env: { ...process.env, DataDirectory: directory, PublicOrigin: base, SiloPort:'11113', GatewayPort:'30002', Logging__LogLevel__Default: 'Warning', Logging__LogLevel__Orleans: 'Warning' }, stdio: ['ignore','pipe','pipe'] });
  server.stdout.on('data', d => output += d); server.stderr.on('data', d => output += d);
  for (let i=0;i<100;i++) { try { if((await fetch(base+'/health')).ok) return; } catch {} if(server.exitCode !== null) throw new Error(output); await new Promise(r=>setTimeout(r,200)); }
  throw new Error(`Startup timed out: ${output}`);
}
async function stop() { if(server && server.exitCode === null) { const done = once(server,'exit'); server.kill('SIGKILL'); await done; } }
async function challenge(username) {const c=await request('/api/auth/challenge','POST',{username});return {kind:c.kind,created_at:c.created_at,tags:c.tags,content:c.content};}
async function login(username,key) {return request('/api/auth/nostr','POST',finalizeEvent(await challenge(username),key));}
try {
  if(local) await start();
  await request('/health');
  assert((await request('/')).includes('Agents have'));checks++;
  assert((await request('/llms.txt')).includes('Registration and sign-in'));checks++;
  await request('/styles.css');
  await request('/app.js');
  await request('/api/me','GET',undefined,undefined,401);
  await request('/api/auth/challenge','POST',{username:'<script>'},undefined,400);
  await request('/api/auth/challenge','POST',{username:alice},undefined,403,{Origin:'https://evil.example'});
  await request('/api/auth/nostr','POST',{kind:'wrong'},undefined,400);
  const aKey=generateSecretKey(),bKey=generateSecretKey();
  const event=finalizeEvent(await challenge(alice),aKey);
  const a=await request('/api/auth/nostr','POST',event);
  assert.equal(a.agent.username,alice);assert.equal(a.token.length,64); checks+=2;
  await request('/api/auth/nostr','POST',event,undefined,400);
  const tampered={...finalizeEvent(await challenge(bob),bKey),sig:'0'.repeat(128)};
  await request('/api/auth/nostr','POST',tampered,undefined,400);
  const b=await login(bob,bKey);
  await request('/api/auth/nostr','POST',finalizeEvent(await challenge(alice),bKey),undefined,409);
  await request('/api/auth/nostr','POST',finalizeEvent(await challenge(`test_c_${suffix}`),aKey),undefined,409);
  await request(`/api/chirper/${alice}/chirps`,'POST',{message:'spoof'},undefined,401);
  await request(`/api/chirper/${alice}/chirps`,'POST',{message:'spoof'},b.token,403);
  await request(`/api/chirper/${alice}/following/${bob}`,'POST',undefined,b.token,403);
  await request(`/api/chirper/${alice}/following/${alice}`,'POST',undefined,a.token,400);
  await request(`/api/chirper/${alice}/timeline`,'GET',undefined,b.token,403);
  await request(`/api/chirper/${alice}/Timeline`,'GET',undefined,undefined,401);
  await request(`/api/chirper/${alice}/TIMELINE`,'GET',undefined,b.token,403);
  await request(`/api/chirper/${alice}/chirps`,'POST',{message:'x'.repeat(281)},a.token,400);
  await request(`/api/chirper/${bob}/following/${alice}`,'POST',undefined,b.token);
  const message='Integration check: unicode <>& " \\ \n 🌱';
  await request(`/api/chirper/${alice}/chirps`,'POST',{message},a.token);
  const timeline=await request(`/api/chirper/${bob}/timeline`,'GET',undefined,b.token);
  assert(timeline.some(p=>p.message===message)); checks++;
  assert((await request('/api/feed')).some(p=>p.publisherUserName===alice));checks++;
  await request(`/api/chirper/${alice}/chirps?count=-4`);
  const malformed=finalizeEvent({...await challenge(alice),created_at:1},aKey);
  await request('/api/auth/nostr','POST',malformed,undefined,400);
  const pending=await challenge(alice);
  if(local) {
    await stop(); await start();
    await request('/api/me','GET',undefined,a.token);
    assert((await request(`/api/chirper/${bob}/following`)).includes(alice));checks++;
    assert((await request(`/api/chirper/${bob}/timeline`,'GET',undefined,b.token)).some(p=>p.message===message));checks++;
    assert((await request(`/api/chirper/${alice}/chirps`)).some(p=>p.message===message));checks++;
    await request(`/api/chirper/${alice}/chirps`,'POST',{message:'Post after restart'},a.token);
    assert((await request(`/api/chirper/${bob}/timeline`,'GET',undefined,b.token)).some(p=>p.message==='Post after restart'));checks++;
    await request('/api/auth/nostr','POST',finalizeEvent(pending,aKey),undefined,400);
    const again=await login(alice,aKey);assert.equal(again.agent.pubkey,a.agent.pubkey);checks++;
    // Raw keys and bearer tokens must never be persisted in the database.
    const bytes=readFileSync(join(directory,'agentai.db'));
    assert(!bytes.includes(Buffer.from(a.token)));assert(!bytes.includes(Buffer.from(b.token)));checks+=2;
  }
  await request(`/api/chirper/${bob}/following/${alice}`,'DELETE',undefined,b.token);
  assert(!(await request(`/api/chirper/${bob}/following`)).includes(alice));checks++;
  await request('/api/auth/session','DELETE',undefined,a.token,204);
  await request('/api/me','GET',undefined,a.token,401);
  console.log(`PASS: ${checks} integration assertions${local?', including abrupt restart and durable recovery':''}`);
} catch(e) {console.error(e);if(local)console.error(output.slice(-9000));process.exitCode=1;}
finally {if(local)await stop();}
