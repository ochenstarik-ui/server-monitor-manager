/**
 * SMM Operator Web Console
 */

(function () {
  'use strict';

  // DOM Elements
  const nodesTableBody = document.getElementById('nodes-table-body');
  const linksTableBody = document.getElementById('links-table-body');
  const nodesCountBadge = document.getElementById('nodes-count');
  const linksCountBadge = document.getElementById('links-count');
  const nodesEmpty = document.getElementById('nodes-empty');
  const linksEmpty = document.getElementById('links-empty');
  const lastUpdatedText = document.getElementById('last-updated-text');
  const globalAlert = document.getElementById('global-alert');

  // Action Elements
  const refreshBtn = document.getElementById('refresh-btn');
  const addNodeBtn = document.getElementById('add-node-btn');

  // Modal Elements
  const modalBackdrop = document.getElementById('add-node-modal');
  const modalCloseBtn = document.getElementById('modal-close-btn');
  const modalCancelBtn = document.getElementById('modal-cancel-btn');
  const enrollmentForm = document.getElementById('enrollment-form');
  const nodeIdInput = document.getElementById('node-id-input');
  const generateCodeBtn = document.getElementById('generate-code-btn');
  const modalError = document.getElementById('modal-error');
  const enrollmentResult = document.getElementById('enrollment-result');
  const resultDoneBtn = document.getElementById('result-done-btn');

  // Result Displays & Copy Buttons
  const caFingerprintDisplay = document.getElementById('ca-fingerprint-display');
  const enrollmentCodeDisplay = document.getElementById('enrollment-code-display');
  const copyFingerprintBtn = document.getElementById('copy-fingerprint-btn');
  const copyCodeBtn = document.getElementById('copy-code-btn');
  const countdownTimer = document.getElementById('countdown-timer');

  // State
  let countdownInterval = null;
  let autoRefreshInterval = null;
  let isModalOpen = false;

  // Initialize
  document.addEventListener('DOMContentLoaded', () => {
    bindEvents();
    loadDashboardData();
    startAutoRefresh();
  });

  function bindEvents() {
    refreshBtn.addEventListener('click', () => {
      loadDashboardData(true);
    });

    addNodeBtn.addEventListener('click', openAddNodeModal);
    modalCloseBtn.addEventListener('click', closeAddNodeModal);
    modalCancelBtn.addEventListener('click', closeAddNodeModal);
    resultDoneBtn.addEventListener('click', closeAddNodeModal);

    modalBackdrop.addEventListener('click', (e) => {
      if (e.target === modalBackdrop) {
        closeAddNodeModal();
      }
    });

    enrollmentForm.addEventListener('submit', handleEnrollmentSubmit);

    copyCodeBtn.addEventListener('click', () => {
      copyToClipboard(enrollmentCodeDisplay.value, copyCodeBtn, 'Скопировать код');
    });

    copyFingerprintBtn.addEventListener('click', () => {
      copyToClipboard(caFingerprintDisplay.textContent, copyFingerprintBtn, 'Копировать отпечаток');
    });

    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && isModalOpen) {
        closeAddNodeModal();
      }
    });
  }

  function startAutoRefresh() {
    if (autoRefreshInterval) clearInterval(autoRefreshInterval);
    autoRefreshInterval = setInterval(() => {
      if (!isModalOpen) {
        loadDashboardData(false);
      }
    }, 10000);
  }

  async function loadDashboardData(showLoadingIndicator = false) {
    if (showLoadingIndicator) {
      refreshBtn.disabled = true;
      refreshBtn.querySelector('.btn-icon').textContent = '⏳';
    }

    try {
      const [agentsRes, linksRes] = await Promise.all([
        fetch('/api/v1/control/agents', { headers: { Accept: 'application/json' } }),
        fetch('/api/v1/control/links', { headers: { Accept: 'application/json' } })
      ]);

      if (agentsRes.status === 401 || linksRes.status === 401) {
        showGlobalAlert('Ошибка аутентификации: требуется клиентский сертификат роли Operator.', 'error');
        return;
      }
      if (agentsRes.status === 403 || linksRes.status === 403) {
        showGlobalAlert('Доступ запрещён: сертификат не имеет роли Operator.', 'error');
        return;
      }

      if (!agentsRes.ok || !linksRes.ok) {
        throw new Error(`Ошибка загрузки данных (${agentsRes.status} / ${linksRes.status})`);
      }

      const agents = await agentsRes.json();
      const links = await linksRes.json();

      renderNodes(agents);
      renderLinks(links);
      hideGlobalAlert();

      const now = new Date();
      lastUpdatedText.textContent = `Обновлено в ${now.toLocaleTimeString()}`;
    } catch (err) {
      console.error('Failed to load dashboard data:', err);
      showGlobalAlert(`Не удалось обновить данные: ${err.message}`, 'error');
    } finally {
      if (showLoadingIndicator) {
        refreshBtn.disabled = false;
        refreshBtn.querySelector('.btn-icon').textContent = '↻';
      }
    }
  }

  function renderNodes(agents) {
    const list = Array.isArray(agents) ? agents : [];
    nodesCountBadge.textContent = list.length;

    if (list.length === 0) {
      nodesTableBody.innerHTML = '';
      nodesEmpty.classList.remove('hidden');
      return;
    }

    nodesEmpty.classList.add('hidden');
    nodesTableBody.innerHTML = list.map(agent => {
      const statusClass = (agent.status || 'unknown').toLowerCase();
      const lastSeen = formatTimestamp(agent.lastSeenAt);
      const certInfo = formatCertInfo(agent);

      return `
        <tr>
          <td class="node-name-cell">${escapeHtml(agent.nodeId || agent.name || '—')}</td>
          <td><span class="status-badge ${escapeHtml(statusClass)}">${escapeHtml(agent.status || 'Unknown')}</span></td>
          <td>${escapeHtml(agent.agentVersion || '—')}</td>
          <td>${lastSeen}</td>
          <td>${certInfo}</td>
        </tr>
      `;
    }).join('');
  }

  function renderLinks(links) {
    const list = Array.isArray(links) ? links : [];
    linksCountBadge.textContent = list.length;

    if (list.length === 0) {
      linksTableBody.innerHTML = '';
      linksEmpty.classList.remove('hidden');
      return;
    }

    linksEmpty.classList.add('hidden');
    linksTableBody.innerHTML = list.map(link => {
      const desiredClass = (link.desiredState || 'unknown').toLowerCase();
      const actualClass = (link.actualState || 'unknown').toLowerCase();
      const expires = formatTimestamp(link.expiresAt);

      return `
        <tr>
          <td class="link-id-cell" title="${escapeHtml(link.id)}">${escapeHtml((link.id || '').substring(0, 8))}...</td>
          <td class="node-name-cell">${escapeHtml(link.sourceNodeId || '—')}</td>
          <td class="node-name-cell">${escapeHtml(link.targetNodeId || '—')}</td>
          <td>${escapeHtml(link.protocol || 'TCP')}/${escapeHtml(String(link.port || 0))}</td>
          <td><span class="status-badge ${escapeHtml(desiredClass)}">${escapeHtml(link.desiredState || '—')}</span></td>
          <td><span class="status-badge ${escapeHtml(actualClass)}">${escapeHtml(link.actualState || '—')}</span></td>
          <td>${expires}</td>
        </tr>
      `;
    }).join('');
  }

  async function handleEnrollmentSubmit(e) {
    e.preventDefault();
    const nodeId = (nodeIdInput.value || '').trim();

    if (!nodeId || !/^[a-z0-9-]{1,63}$/.test(nodeId)) {
      showModalError('Имя узла должно содержать от 1 до 63 строчных латинских букв, цифр или дефисов.');
      return;
    }

    hideModalError();
    generateCodeBtn.disabled = true;
    generateCodeBtn.textContent = 'Генерация...';

    try {
      const res = await fetch(`/api/v1/control/agents/${encodeURIComponent(nodeId)}/enrollment-code`, {
        method: 'POST',
        headers: {
          'Accept': 'application/json'
        }
      });

      if (res.status === 401) {
        showModalError('Требуется клиентский сертификат роли Operator.');
        return;
      }
      if (res.status === 403) {
        showModalError('Доступ запрещен: роль не является Operator.');
        return;
      }
      if (res.status === 429) {
        showModalError('Превышен лимит запросов на выпуск кодов (10/мин). Подождите перед повторной попыткой.');
        return;
      }

      const data = await res.json();
      if (!res.ok) {
        const errorDetail = data?.detail || data?.title || 'Ошибка выпуска кода регистрации';
        showModalError(errorDetail);
        return;
      }

      displayEnrollmentResult(data);
    } catch (err) {
      console.error('Enrollment code creation error:', err);
      showModalError(`Сетевая ошибка: ${err.message}`);
    } finally {
      generateCodeBtn.disabled = false;
      generateCodeBtn.textContent = 'Сгенерировать код';
    }
  }

  function displayEnrollmentResult(data) {
    enrollmentForm.classList.add('hidden');
    enrollmentResult.classList.remove('hidden');

    caFingerprintDisplay.textContent = data.certificateAuthorityFingerprintSha256 || '—';
    enrollmentCodeDisplay.value = data.code || '';

    // Start 10-minute countdown
    const expiresAt = data.expiresAt ? new Date(data.expiresAt) : new Date(Date.now() + 10 * 60 * 1000);
    startCountdown(expiresAt);
  }

  function startCountdown(targetDate) {
    if (countdownInterval) clearInterval(countdownInterval);

    function update() {
      const now = new Date();
      const remainingMs = targetDate.getTime() - now.getTime();

      if (remainingMs <= 0) {
        countdownTimer.textContent = '00:00 (истёк)';
        countdownTimer.style.backgroundColor = 'var(--danger-bg)';
        countdownTimer.style.color = 'var(--danger-text)';
        clearInterval(countdownInterval);
        return;
      }

      const totalSec = Math.floor(remainingMs / 1000);
      const min = Math.floor(totalSec / 60);
      const sec = totalSec % 60;
      countdownTimer.textContent = `${String(min).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
    }

    update();
    countdownInterval = setInterval(update, 1000);
  }

  function openAddNodeModal() {
    isModalOpen = true;
    enrollmentForm.reset();
    enrollmentForm.classList.remove('hidden');
    enrollmentResult.classList.add('hidden');
    hideModalError();
    modalBackdrop.classList.remove('hidden');
    nodeIdInput.focus();
  }

  function closeAddNodeModal() {
    isModalOpen = false;
    modalBackdrop.classList.add('hidden');
    if (countdownInterval) {
      clearInterval(countdownInterval);
      countdownInterval = null;
    }
    loadDashboardData();
  }

  async function copyToClipboard(text, btnElement, defaultLabel) {
    if (!text) return;
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
      } else {
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.select();
        document.execCommand('copy');
        document.body.removeChild(textarea);
      }

      btnElement.textContent = '✓ Скопировано!';
      btnElement.classList.add('btn-success');
      setTimeout(() => {
        btnElement.textContent = defaultLabel;
        btnElement.classList.remove('btn-success');
      }, 2000);
    } catch (err) {
      console.error('Copy failed:', err);
      btnElement.textContent = 'Ошибка копирования';
      setTimeout(() => {
        btnElement.textContent = defaultLabel;
      }, 2000);
    }
  }

  function showModalError(msg) {
    modalError.textContent = msg;
    modalError.classList.remove('hidden');
  }

  function hideModalError() {
    modalError.textContent = '';
    modalError.classList.add('hidden');
  }

  function showGlobalAlert(msg, type = 'error') {
    globalAlert.textContent = msg;
    globalAlert.className = `alert alert-${type}`;
    globalAlert.classList.remove('hidden');
  }

  function hideGlobalAlert() {
    globalAlert.classList.add('hidden');
  }

  function formatTimestamp(isoString) {
    if (!isoString) return '<span class="text-muted">—</span>';
    const date = new Date(isoString);
    if (isNaN(date.getTime())) return '<span class="text-muted">—</span>';

    const diffSec = Math.floor((Date.now() - date.getTime()) / 1000);
    if (diffSec < 0) {
      return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }
    if (diffSec < 60) return `${diffSec} сек. назад`;
    if (diffSec < 3600) return `${Math.floor(diffSec / 60)} мин. назад`;
    if (diffSec < 86400) return `${Math.floor(diffSec / 3600)} ч. назад`;

    return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  function formatCertInfo(agent) {
    if (typeof agent.certificateRemainingDays === 'number') {
      const days = agent.certificateRemainingDays;
      if (days < 0) return '<span class="status-badge failed">Истёк</span>';
      if (days <= 7) return `<span class="status-badge pending">${days} дн.</span>`;
      return `<span class="status-badge active">${days} дн.</span>`;
    }
    if (agent.certificateExpiresAt) {
      const expDate = new Date(agent.certificateExpiresAt);
      return expDate.toLocaleDateString();
    }
    return '<span class="text-muted">—</span>';
  }

  function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }
})();
