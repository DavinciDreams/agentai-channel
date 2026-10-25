import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { generateSecretKey, finalizeEvent } from 'nostr-tools/pure';
import { exerciseProfileUI } from './profile-ui.mjs';

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
  const homepage=await request('/');
  assert(homepage.includes('Agents have'));checks++;
  for(const filename of ['app.js','styles.css']){
    const path=homepage.match(new RegExp(`/(?:${filename.replace('.','\\.')})\\?v=[a-f0-9]{16}`))?.[0];
    assert(path,`Homepage must version ${filename} by its content`);checks++;
    const source=await request(path);
    assert.equal(path.split('=')[1],createHash('sha256').update(source).digest('hex').slice(0,16));checks++;
  }
  assert.equal(await request('/index.html'),homepage);checks++;
  for(const path of ['/','/index.html','/llms.txt']){
    const response=await fetch(base+path);
    assert.equal(response.status,200);assert.equal(response.headers.get('cache-control'),'no-cache');checks+=2;
  }
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
  const initialProfile=await request(`/api/agents/${alice}`);
  assert.equal(initialProfile.displayName,'');assert.equal(initialProfile.bio,'');assert.equal(initialProfile.website,'');checks+=3;
  await request('/api/agents/no_such_agent','GET',undefined,undefined,404);
  await request(`/api/agents/${alice}`,'PATCH',{bio:'spoof'},undefined,401);
  await request(`/api/agents/${alice}`,'PATCH',{bio:'spoof'},b.token,403);
  await request(`/api/agents/${alice.toUpperCase()}`,'PATCH',{bio:'spoof'},a.token,403);
  const label=`Research Peer ${suffix}`;
  let profile=await request(`/api/agents/${alice}`,'PATCH',{displayName:` ${label} `,bio:'Research, reproducible experiments, and <useful> failures.',website:'https://example.com/agent'},a.token);
  assert.equal(profile.displayName,label);assert.equal(profile.pubkey,a.agent.pubkey);assert.equal(profile.createdAt,initialProfile.createdAt);checks+=3;
  await Promise.all([
    request(`/api/agents/${alice}`,'PATCH',{bio:'A collaborative research agent.'},a.token),
    request(`/api/agents/${alice}`,'PATCH',{website:'https://example.com/source'},a.token)
  ]);
  profile=await request(`/api/agents/${alice}`);
  assert.equal(profile.bio,'A collaborative research agent.');assert.equal(profile.website,'https://example.com/source');assert.equal(profile.displayName,label);checks+=3;
  const currentProfile=profile;
  for(const invalid of [{displayName:'x'.repeat(61)},{bio:'x'.repeat(281)},{website:'x'.repeat(2049)},{website:'javascript:alert(1)'},{website:'data:text/html,hello'},{website:'https://user:password@example.com'},{bio:42}])
    await request(`/api/agents/${alice}`,'PATCH',invalid,a.token,400);
  assert.deepEqual(await request(`/api/agents/${alice}`),currentProfile);checks++;
  const noRename=await request(`/api/agents/${alice}`,'PATCH',{username:bob,pubkey:b.agent.pubkey},a.token);
  assert.equal(noRename.username,alice);assert.equal(noRename.pubkey,a.agent.pubkey);checks+=2;
  assert((await request(`/api/agents?q=${encodeURIComponent(label.toLowerCase())}`)).some(p=>p.username===alice));checks++;
  assert((await request(`/api/agents?q=%40${alice}`)).some(p=>p.username===alice));checks++;
  assert.equal((await request('/api/agents?q=%25')).length,0);checks++;
  assert.equal((await request('/api/agents?q=%27%20OR%201%3D1--')).length,0);checks++;
  await request(`/api/agents/${alice}`,'PATCH',{website:''},a.token);
  assert.equal((await request(`/api/agents/${alice}`)).website,'');checks++;
  if(local){
    const file=join(directory,'test-identity.json');
    writeFileSync(file,JSON.stringify({username:alice,secretHex:Buffer.from(aKey).toString('hex'),token:a.token,expiresAt:Date.now()+86400000,base}),{mode:0o600});
    const cli=JSON.parse(execFileSync(process.execPath,['tools/agent.mjs','profile','--bio','Updated through the agent CLI.'],{cwd:root,env:{...process.env,AGENTAI_URL:base,AGENTAI_IDENTITY:file},encoding:'utf8'}));
    assert.equal(cli.bio,'Updated through the agent CLI.');assert.equal(cli.displayName,label);checks+=2;
  }
  checks+=await exerciseProfileUI(base,alice,bob,aKey);
  const profileBeforeRestart=await request(`/api/agents/${alice}`);
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
    assert.deepEqual(await request(`/api/agents/${alice}`),profileBeforeRestart);checks++;
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
