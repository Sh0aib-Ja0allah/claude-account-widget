// Claude Code status line. Saves plan usage for the desktop widget and prints a short footer.
const fs = require('fs');
const os = require('os');
const path = require('path');

const dir = __dirname;
const usageFile = path.join(dir, 'usage.json');

function currentAccount() {
  try {
    const text = fs.readFileSync(path.join(os.homedir(), '.claude.json'), 'utf8');
    const match = text.match(/"oauthAccount"\s*:\s*\{[^{}]*?"emailAddress"\s*:\s*"([^"]+)"/);
    return match ? match[1] : '';
  } catch {
    return '';
  }
}

function readPrevious() {
  try {
    return JSON.parse(fs.readFileSync(usageFile, 'utf8'));
  } catch {
    return {};
  }
}

function pickWindow(window, previous) {
  if (window && typeof window.used_percentage === 'number') {
    return { used: window.used_percentage, resets: window.resets_at || 0 };
  }
  return previous || null;
}

function save(input) {
  const limits = input.rate_limits;
  if (!limits) return;
  const account = currentAccount();
  const previous = readPrevious();
  const sameAccount = previous.account === account;
  const data = {
    account,
    updated: Date.now(),
    five_hour: pickWindow(limits.five_hour, sameAccount ? previous.five_hour : null),
    seven_day: pickWindow(limits.seven_day, sameAccount ? previous.seven_day : null),
  };
  const temp = `${usageFile}.${process.pid}.tmp`;
  fs.writeFileSync(temp, JSON.stringify(data));
  fs.renameSync(temp, usageFile);
}

function footer(input) {
  const parts = [];
  if (input.model && input.model.display_name) parts.push(input.model.display_name);
  const limits = input.rate_limits || {};
  if (limits.five_hour) parts.push(`5h ${Math.round(limits.five_hour.used_percentage)}%`);
  if (limits.seven_day) parts.push(`7d ${Math.round(limits.seven_day.used_percentage)}%`);
  return parts.join(' · ');
}

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => { raw += chunk; });
process.stdin.on('end', () => {
  let input = {};
  try {
    input = JSON.parse(raw.replace(/^﻿/, ''));
    save(input);
  } catch {
    // A status line must never break the session; the widget keeps its last values.
  }
  console.log(footer(input));
});
