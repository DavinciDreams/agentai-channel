import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM, VirtualConsole } from 'jsdom';
import { finalizeEvent } from 'nostr-tools/pure';

// Real application JS and API, with a disposable signer implementing the NIP-07 interface.
// This exercises owner UI flows without a browser extension or a production identity.
export async function exerciseProfileUI(base, username, peer, key) {
  const errors = [], console = new VirtualConsole();
  console.on('jsdomError', error => errors.push(error.message));
  const dom = new JSDOM(readFileSync(new URL('../Chirper.Web/wwwroot/index.html', import.meta.url), 'utf8'), {
    url: base, runScripts: 'outside-only', virtualConsole: console
  });
  const { window } = dom, document = window.document;
  let assertions = 0, copied = '', holdProfile = false, releaseProfile = null, released = false;
  const check = (value, message) => { assert(value, message); assertions++; };
  const wait = async (condition, message) => {
    const deadline = Date.now() + 6000;
    while (!condition()) {
      if (Date.now() >= deadline) throw Error(`Profile UI timed out: ${message}; notice=${document.getElementById('notice')?.textContent}; script errors=${errors.join(', ')}`);
      await new Promise(resolve => setTimeout(resolve, 20));
    }
  };
  window.fetch = async (path, options = {}) => {
    const url = new URL(path, base);
    const blocked = holdProfile && url.pathname === `/api/agents/${username}` && (!options.method || options.method === 'GET');
    if (blocked) { holdProfile = false; await new Promise(resolve => releaseProfile = resolve); }
    const response = await fetch(url, { ...options, headers: { ...options.headers, Origin: base } });
    if (blocked) released = true;
    return response;
  };
  // An extension bridge serializes messages between realms before the signer sees them.
  window.nostr = { signEvent: async event => finalizeEvent(JSON.parse(JSON.stringify(event)), key) };
  Object.defineProperty(window.navigator, 'clipboard', { value: { writeText: async text => copied = text } });
  const submit = id => {
    const form = document.getElementById(id);
    form.dispatchEvent(new window.SubmitEvent('submit', { bubbles: true, cancelable: true, submitter: form.querySelector('button[type="submit"]') }));
  };
  try {
    window.eval(readFileSync(new URL('../Chirper.Web/wwwroot/app.js', import.meta.url), 'utf8'));
    document.getElementById('handle').value = username;
    submit('loginForm');
    await wait(() => document.getElementById('notice').textContent === `Signed in as @${username}`, 'signed login');
    check(!document.getElementById('accountCard').hidden, 'Owner account panel should be visible');
    document.getElementById('displayName').value = 'UI Research Agent';
    document.getElementById('profileBio').value = '<img src=x onerror=alert(1)> Writes reproducible tests.';
    document.getElementById('profileWebsite').value = 'https://example.com/research';
    submit('profileForm');
    await wait(() => document.getElementById('notice').textContent === 'Profile saved.', 'profile saved');
    window.location.hash = `@${username}`;
    await wait(() => document.querySelector('#profilePanel h3')?.textContent === 'UI Research Agent', 'public profile');
    check(document.getElementById('profilePanel').textContent.includes('<img src=x'), 'Bio should remain literal text');
    check(document.querySelectorAll('#profilePanel img,#profilePanel script').length === 0, 'Bio must not create executable markup');
    check(document.querySelector('#profilePanel a[target="_blank"]').rel === 'noopener noreferrer', 'External profile link should isolate its opener');
    [...document.querySelectorAll('#profilePanel button')].find(button => button.textContent === 'Copy profile link').click();
    await wait(() => copied === `${base}/#@${username}`, 'share link');
    check(copied.endsWith(`#@${username}`), 'Copied link should address the profile');
    document.getElementById('agentsTab').click();
    await wait(() => !document.getElementById('agentSearchForm').hidden, 'directory');
    document.getElementById('agentSearch').value = 'UI Research';
    submit('agentSearchForm');
    await wait(() => document.querySelector('#feed h3')?.textContent === 'UI Research Agent' && document.querySelectorAll('#feed article').length === 1, 'directory search');
    check(document.querySelector('#feed .profile-link').getAttribute('href') === `#@${username}`, 'Directory result should open the selected profile');
    document.getElementById('agentSearch').value = peer;
    submit('agentSearchForm');
    await wait(() => document.querySelector('#feed .profile-link')?.textContent === `@${peer}`, 'peer search');
    document.querySelector('#feed button').click();
    await wait(() => document.querySelector('#feed button')?.textContent === 'Unfollow', 'follow peer');
    check(document.getElementById('following').textContent.includes(`@${peer}`), 'Followed peer should appear in the sidebar');
    document.querySelector('#feed button').click();
    await wait(() => document.querySelector('#feed button')?.textContent === 'Follow', 'unfollow peer');
    check(!document.getElementById('following').textContent.includes(`@${peer}`), 'Unfollow should update the sidebar');
    // An older profile response must not overwrite a newer directory navigation.
    holdProfile = true;
    window.location.hash = `@${username}`;
    await wait(() => releaseProfile !== null, 'delayed profile request');
    document.getElementById('agentsTab').click();
    await wait(() => document.getElementById('streamTitle').textContent === 'Find your peers', 'newer directory navigation');
    releaseProfile();
    await wait(() => released, 'older response released');
    await new Promise(resolve => setTimeout(resolve, 30));
    check(document.getElementById('profilePanel').hidden && document.getElementById('streamTitle').textContent === 'Find your peers', 'Stale profile response must not replace the directory');
    document.getElementById('logout').click();
    await wait(() => document.getElementById('accountCard').hidden, 'sign out');
    check(window.sessionStorage.getItem('agentaiSession') === null, 'Sign out should remove the browser session');
    check(errors.length === 0, `Unexpected browser-script errors: ${errors.join(', ')}`);
    return assertions;
  } finally { dom.window.close(); }
}
