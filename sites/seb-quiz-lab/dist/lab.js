'use strict';

const quiz = document.querySelector('#quiz');
const start = document.querySelector('#start');
const log = document.querySelector('#log');
const status = document.querySelector('#state');
const connection = document.querySelector('#connection');
const questions = [...quiz.querySelectorAll('[data-question]')];
let attempt = null;
let away = false;
let viewed = new Set();

const descriptions = {
  started: 'Bắt đầu lượt thử',
  viewed: 'Đã xem câu',
  answered: 'Đã trả lời câu',
  stopped: 'Rời trang bài kiểm tra',
  resumed: 'Quay lại trang bài kiểm tra',
  shortcut: 'Trang nhận tổ hợp phím',
  submitted: 'Đã nộp bài thử'
};

function record(type, detail = {}) {
  if (!attempt) return;
  const event = { attempt, type, time: new Date().toISOString(), ...detail };
  const row = document.createElement('li');
  const time = document.createElement('time');
  time.dateTime = event.time;
  time.textContent = new Date(event.time).toLocaleTimeString('vi-VN');
  const label = descriptions[type] + (event.question ? ' ' + event.question : '');
  const detailLabel = event.signal || event.shortcut;
  row.append(time, document.createTextNode(label + (detailLabel ? ' · ' + detailLabel : '')));
  log.prepend(row);
  fetch('/events', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(event),
    keepalive: true
  }).then(response => {
    if (!response.ok) throw new Error('Log rejected');
  }).catch(() => {
    connection.textContent = 'Có sự kiện chưa lưu vào máy chủ. Kiểm tra máy chủ cục bộ.';
  });
}

function updatePresence(signal) {
  if (!attempt) return;
  const isAway = document.hidden || !document.hasFocus();
  if (isAway === away) return;
  away = isAway;
  record(away ? 'stopped' : 'resumed', { signal });
}

function markViewed(fieldset) {
  const question = Number(fieldset.dataset.question);
  if (!attempt || viewed.has(question)) return;
  viewed.add(question);
  record('viewed', { question });
}

document.addEventListener('visibilitychange', () => updatePresence('visibilitychange'));
window.addEventListener('blur', () => updatePresence('blur'));
window.addEventListener('focus', () => updatePresence('focus'));
window.addEventListener('pagehide', () => {
  if (attempt && !away) {
    away = true;
    record('stopped', { signal: 'pagehide' });
  }
});
window.addEventListener('pageshow', () => updatePresence('pageshow'));
window.addEventListener('keydown', event => {
  if (!attempt || event.repeat || event.isComposing || !event.ctrlKey || !event.shiftKey || event.altKey || event.metaKey) return;
  const key = event.key.toUpperCase();
  if (key === 'J' || key === 'K' || key === 'H') {
    record('shortcut', { shortcut: 'Ctrl + Shift + ' + key });
  }
}, { capture: true });

for (const fieldset of questions) {
  fieldset.addEventListener('focusin', () => markViewed(fieldset));
  fieldset.addEventListener('pointerdown', () => markViewed(fieldset));
  fieldset.addEventListener('change', () => {
    markViewed(fieldset);
    record('answered', { question: Number(fieldset.dataset.question) });
  });
}

start.addEventListener('click', () => {
  attempt = crypto.randomUUID();
  viewed = new Set();
  away = false;
  log.replaceChildren();
  connection.textContent = '';
  quiz.reset();
  quiz.hidden = false;
  start.hidden = true;
  status.textContent = 'Đang làm';
  record('started');
});

quiz.addEventListener('submit', event => {
  event.preventDefault();
  record('submitted');
  attempt = null;
  quiz.hidden = true;
  start.textContent = 'Bắt đầu lượt thử mới';
  start.hidden = false;
  status.textContent = 'Đã nộp';
});
