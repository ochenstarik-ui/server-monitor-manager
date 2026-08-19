/**
 * SMM Operator Web Console
 */

(function () {
  'use strict';

  // DOM Elements
  const nodesTableBody = document.getElementById('nodes-table-body');
  const linksTableBody = document.getElementById('links-table-body');
  const eventsTableBody = document.getElementById('events-table-body');
  const nodesCountBadge = document.getElementById('nodes-count');
  const linksCountBadge = document.getElementById('links-count');
  const eventsCountBadge = document.getElementById('events-count');
  const nodesEmpty = document.getElementById('nodes-empty');
  const linksEmpty = document.getElementById('links-empty');
  const eventsEmpty = document.getElementById('events-empty');
  const lastUpdatedText = document.getElementById('last-updated-text');
  const globalAlert = document.getElementById('global-alert');
  const connectionStatus = document.getElementById('connection-status');
  const testingBanner = document.getElementById('testing-auth-warning');
  const nodesDatalist = document.getElementById('nodes-datalist');

  // Action Elements
  const refreshBtn = document.getElementById('refresh-btn');
  const addNodeBtn = document.getElementById('add-node-btn');
  const createLinkBtn = document.getElementById('create-link-btn');
  const headerCreateLinkBtn = document.getElementById('header-create-link-btn');
  const logoutBtn = document.getElementById('logout-btn');

  // Add Node Modal Elements
  const addNodeModal = document.getElementById('add-node-modal');
  const modalCloseBtn = document.getElementById('modal-close-btn');
  const modalCancelBtn = document.getElementById('modal-cancel-btn');
  const enrollmentForm = document.getElementById('enrollment-form');
  const nodeIdInput = document.getElementById('node-id-input');
  const generateCodeBtn = document.getElementById('generate-code-btn');
  const modalError = document.getElementById('modal-error');
  const enrollmentResult = document.getElementById('enrollment-result');
  const resultDoneBtn = document.getElementById('result-done-btn');
  const caFingerprintDisplay = document.getElementById('ca-fingerprint-display');
  const enrollmentCodeDisplay = document.getElementById('enrollment-code-display');
  const copyFingerprintBtn = document.getElementById('copy-fingerprint-btn');
  const copyCodeBtn = document.getElementById('copy-code-btn');
  const countdownTimer = document.getElementById('countdown-timer');

  // Create Link Modal Elements
  const createLinkModal = document.getElementById('create-link-modal');
  const linkModalCloseBtn = document.getElementById('link-modal-close-btn');
  const linkModalCancelBtn = document.getElementById('link-modal-cancel-btn');
  const createLinkForm = document.getElementById('create-link-form');
  const linkSourceNode = document.getElementById('link-source-node');
  const linkTargetNode = document.getElementById('link-target-node');
  const linkProtocol = document.getElementById('link-protocol');
  const linkPort = document.getElementById('link-port');
  const linkTtl = document.getElementById('link-ttl');
  const linkReason = document.getElementById('link-reason');
  const linkDirectionSummary = document.getElementById('link-direction-summary');
  const linkModalError = document.getElementById('link-modal-error');
  const linkModalSubmitBtn = document.getElementById('link-modal-submit-btn');

  // Login Modal Elements
  const loginModal = document.getElementById('login-modal');
  const loginForm = document.getElementById('login-form');
  const loginUsername = document.getElementById('login-username');
  const loginPassword = document.getElementById('login-password');
  const loginSubmitBtn = document.getElementById('login-submit-btn');
  const loginError = document.getElementById('login-error');

  // State
  let countdownInterval = null;
  let autoRefreshInterval = null;
  let isModalOpen = false;
  let isPasswordLoginEnabled = false;
  let currentLinkCreationIdempotencyKey = null;
  let knownNodes = [];
  let eventsList = [];
  let eventStreamAbortController = null;

  const TOKEN_KEY = 'smm_session_token';
  const MAX_EVENTS_IN_JOURNAL = 100;

  // Initialize
  document.addEventListener('DOMContentLoaded', async () => {
    bindEvents();
    await checkAuthStatus();
    await loadDashboardData();
    startAutoRefresh();
    startEventStream();
  });

  function bindEvents() {
    refreshBtn.addEventListener('click', () => {
      loadDashboardData(true);
    });

    // Add Node modal
    addNodeBtn.addEventListener('click', openAddNodeModal);
    modalCloseBtn.addEventListener('click', closeAddNodeModal);
    modalCancelBtn.addEventListener('click', closeAddNodeModal);
    resultDoneBtn.addEventListener('click', closeAddNodeModal);
    addNodeModal.addEventListener('click', (e) => {
      if (e.target === addNodeModal) {
        closeAddNodeModal();
      }
    });
    enrollmentForm.addEventListener('submit', handleEnrollmentSubmit);

    // Create Link modal
    if (createLinkBtn) {
      createLinkBtn.addEventListener('click', openCreateLinkModal);
    }
    if (headerCreateLinkBtn) {
      headerCreateLinkBtn.addEventListener('click', openCreateLinkModal);
    }
    linkModalCloseBtn.addEventListener('click', closeCreateLinkModal);
    linkModalCancelBtn.addEventListener('click', closeCreateLinkModal);
    createLinkModal.addEventListener('click', (e) => {
      if (e.target === createLinkModal) {
        closeCreateLinkModal();
      }
    });
    createLinkForm.addEventListener('submit', handleCreateLinkSubmit);

    // Live update of Link direction summary
    const linkInputElements = [linkSourceNode, linkTargetNode, linkProtocol, linkPort, linkTtl];
    linkInputElements.forEach(el => {
      if (el) {
        el.addEventListener('input', updateLinkDirectionSummary);
        el.addEventListener('change', updateLinkDirectionSummary);
      }
    });

    // Links table action (Emergency Disable button delegation)
    linksTableBody.addEventListener('click', handleLinksTableClick);

    // Login & Logout
    if (loginForm) {
      loginForm.addEventListener('submit', handleLoginSubmit);
    }
    if (logoutBtn) {
      logoutBtn.addEventListener('click', handleLogout);
    }

    // Copy buttons
    copyCodeBtn.addEventListener('click', () => {
      copyToClipboard(enrollmentCodeDisplay.value, copyCodeBtn, 'Скопировать код');
    });

    copyFingerprintBtn.addEventListener('click', () => {
      copyToClipboard(caFingerprintDisplay.textContent, copyFingerprintBtn, 'Копировать отпечаток');
    });

    // Global keyboard handling
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && isModalOpen) {
        if (!addNodeModal.classList.contains('hidden')) closeAddNodeModal();
        if (!createLinkModal.classList.contains('hidden')) closeCreateLinkModal();
      }
    });
  }

  function generateUuid() {
    if (typeof crypto !== 'undefined' && crypto.randomUUID) {
      return crypto.randomUUID();
    }
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
      const r = Math.random() * 16 | 0;
      const v = c === 'x' ? r : (r & 0x3 | 0x8);
      return v.toString(16);
    });
  }

  async function checkAuthStatus() {
    try {
      const res = await fetch('/api/v1/auth/status');
      if (res.ok) {
        const data = await res.json();
        isPasswordLoginEnabled = !!data?.enabledForTesting;
        if (isPasswordLoginEnabled && testingBanner) {
          testingBanner.classList.remove('hidden');
        }
      }
    } catch {
      // Ignored if status endpoint unavailable
    }
  }

  function getStoredToken() {
    return sessionStorage.getItem(TOKEN_KEY) || '';
  }

  function setStoredToken(token) {
    if (token) {
      sessionStorage.setItem(TOKEN_KEY, token);
    } else {
      sessionStorage.removeItem(TOKEN_KEY);
    }
  }

  async function authFetch(url, options = {}) {
    const headers = options.headers ? { ...options.headers } : {};
    const token = getStoredToken();
    if (token && !headers['Authorization']) {
      headers['Authorization'] = `Bearer ${token}`;
    }
    if (!headers['Accept']) {
      headers['Accept'] = 'application/json';
    }

    return await fetch(url, { ...options, headers });
  }

  function startAutoRefresh() {
    if (autoRefreshInterval) clearInterval(autoRefreshInterval);
    autoRefreshInterval = setInterval(() => {
      if (!isModalOpen && (!loginModal || loginModal.classList.contains('hidden'))) {
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
        authFetch('/api/v1/control/agents'),
        authFetch('/api/v1/control/links')
      ]);

      if (agentsRes.status === 401 || linksRes.status === 401) {
        if (isPasswordLoginEnabled && !getStoredToken()) {
          showLoginModal();
          return;
        }
        showGlobalAlert('Ошибка аутентификации: требуется клиентский сертификат роли Operator или авторизация по паролю.', 'error');
        return;
      }
      if (agentsRes.status === 403 || linksRes.status === 403) {
        showGlobalAlert('Доступ запрещён: пользователь или сертификат не имеет роли Operator.', 'error');
        return;
      }

      if (!agentsRes.ok || !linksRes.ok) {
        throw new Error(`Ошибка загрузки данных (${agentsRes.status} / ${linksRes.status})`);
      }

      const agents = await agentsRes.json();
      const links = await linksRes.json();

      updateKnownNodes(agents);
      renderNodes(agents);
      renderLinks(links);
      hideGlobalAlert();
      hideLoginModal();

      if (getStoredToken() && logoutBtn) {
        logoutBtn.classList.remove('hidden');
        if (connectionStatus) {
          connectionStatus.textContent = 'Пароль (Operator)';
        }
      }

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

  function updateKnownNodes(agents) {
    const list = Array.isArray(agents) ? agents : [];
    knownNodes = list.map(a => a.nodeId || a.name).filter(Boolean);
    if (nodesDatalist) {
      nodesDatalist.innerHTML = knownNodes.map(nodeId =>
        `<option value="${escapeHtml(nodeId)}"></option>`
      ).join('');
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
      const isMismatch = (link.desiredState || '').toLowerCase() !== (link.actualState || '').toLowerCase();
      const expiresFormatted = formatExpiration(link.expiresAt);
      const isDisabled = (link.desiredState || '').toLowerCase() === 'disabled';

      const errorHtml = link.lastError
        ? `<div class="link-error-msg" title="${escapeHtml(link.lastError)}">⚠️ ${escapeHtml(link.lastError)}</div>`
        : '';

      const mismatchHtml = isMismatch
        ? `<span class="state-mismatch-badge" title="Желаемое и фактическое состояния не согласованы">⚠️ Не синхронизировано</span>`
        : '';

      const actionHtml = !isDisabled
        ? `<button type="button" class="btn btn-sm btn-danger btn-disable-link"
             data-link-id="${escapeHtml(link.id)}"
             data-source="${escapeHtml(link.sourceNodeId)}"
             data-target="${escapeHtml(link.targetNodeId)}"
             data-protocol="${escapeHtml(link.protocol)}"
             data-port="${escapeHtml(String(link.port))}"
             title="Мгновенное аварийное отключение доступа">
             Отключить
           </button>`
        : `<span class="text-muted">Отключено</span>`;

      return `
        <tr>
          <td class="link-id-cell" title="${escapeHtml(link.id)}">${escapeHtml((link.id || '').substring(0, 8))}...</td>
          <td class="node-name-cell">${escapeHtml(link.sourceNodeId || '—')}</td>
          <td class="node-name-cell">${escapeHtml(link.targetNodeId || '—')}</td>
          <td>${escapeHtml((link.protocol || 'tcp').toUpperCase())}/${escapeHtml(String(link.port || 0))}</td>
          <td>
            <div class="state-cell">
              <span class="status-badge ${escapeHtml(desiredClass)}">${escapeHtml(link.desiredState || '—')}</span>
            </div>
          </td>
          <td>
            <div class="state-cell">
              <span class="status-badge ${escapeHtml(actualClass)}">${escapeHtml(link.actualState || '—')}</span>
              ${mismatchHtml}
              ${errorHtml}
            </div>
          </td>
          <td>${escapeHtml(link.reason || '—')}</td>
          <td>${expiresFormatted}</td>
          <td>${actionHtml}</td>
        </tr>
      `;
    }).join('');
  }

  // Real-time Events Stream
  async function startEventStream() {
    if (eventStreamAbortController) {
      eventStreamAbortController.abort();
    }
    eventStreamAbortController = new AbortController();
    const signal = eventStreamAbortController.signal;

    try {
      const res = await authFetch('/api/v1/control/events', { signal });
      if (!res.ok || !res.body) {
        return;
      }

      const reader = res.body.getReader();
      const decoder = new TextDecoder('utf-8');
      let buffer = '';

      // Clear loading state
      if (eventsList.length === 0) {
        eventsTableBody.innerHTML = '';
        eventsEmpty.classList.remove('hidden');
      }

      while (!signal.aborted) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop(); // Keep partial line in buffer

        for (const line of lines) {
          const trimmed = line.trim();
          if (!trimmed) continue;
          try {
            const ev = JSON.parse(trimmed);
            addControlEvent(ev);
          } catch (e) {
            console.debug('Failed to parse NDJSON event line:', e);
          }
        }
      }
    } catch (err) {
      if (signal.aborted) return;
      console.debug('Event stream disconnected:', err);
    } finally {
      if (!signal.aborted) {
        setTimeout(startEventStream, 5000);
      }
    }
  }

  function addControlEvent(ev) {
    eventsList.unshift(ev);
    if (eventsList.length > MAX_EVENTS_IN_JOURNAL) {
      eventsList.pop();
    }
    renderEvents();

    // Trigger dashboard refresh if topology changed
    const eventType = (ev.type || '').toLowerCase();
    if (eventType.startsWith('link.') || eventType.startsWith('agent.')) {
      loadDashboardData(false);
    }
  }

  function renderEvents() {
    eventsCountBadge.textContent = eventsList.length;

    if (eventsList.length === 0) {
      eventsTableBody.innerHTML = '';
      eventsEmpty.classList.remove('hidden');
      return;
    }

    eventsEmpty.classList.add('hidden');
    eventsTableBody.innerHTML = eventsList.map(ev => {
      const timeStr = formatTimestamp(ev.recordedAt);
      let payloadSummary = '—';
      if (ev.payloadJson) {
        try {
          const parsed = JSON.parse(ev.payloadJson);
          payloadSummary = typeof parsed === 'object'
            ? JSON.stringify(parsed)
            : String(parsed);
        } catch {
          payloadSummary = ev.payloadJson;
        }
      }

      return `
        <tr>
          <td>${timeStr}</td>
          <td><span class="event-type-badge">${escapeHtml(ev.type || 'unknown')}</span></td>
          <td class="node-name-cell">${escapeHtml(ev.subject || '—')}</td>
          <td><div class="event-payload-box" title="${escapeHtml(payloadSummary)}">${escapeHtml(payloadSummary)}</div></td>
        </tr>
      `;
    }).join('');
  }

  // Link Creation Modal & Form Handling
  function openCreateLinkModal() {
    isModalOpen = true;
    currentLinkCreationIdempotencyKey = generateUuid();
    createLinkForm.reset();
    linkProtocol.value = 'tcp';
    linkPort.value = '8080';
    linkTtl.value = '60';
    hideLinkModalError();
    updateLinkDirectionSummary();
    createLinkModal.classList.remove('hidden');
    linkSourceNode.focus();
  }

  function closeCreateLinkModal() {
    isModalOpen = false;
    createLinkModal.classList.add('hidden');
    hideLinkModalError();
  }

  function updateLinkDirectionSummary() {
    const src = (linkSourceNode.value || '').trim();
    const dst = (linkTargetNode.value || '').trim();
    const proto = (linkProtocol.value || 'TCP').toUpperCase();
    const port = (linkPort.value || '').trim();
    const ttl = (linkTtl.value || '').trim();

    if (!src && !dst) {
      linkDirectionSummary.className = 'direction-summary-text';
      linkDirectionSummary.innerHTML = 'Укажите узел-источник и узел-назначение для проверки направления связи.';
      return;
    }

    if (src && dst && src === dst) {
      linkDirectionSummary.className = 'direction-summary-text warning';
      linkDirectionSummary.innerHTML = `⚠️ <strong>Внимание:</strong> узел-источник и назначение совпадают (<code>${escapeHtml(src)}</code>). Правило должно соединять два разных узла!`;
      return;
    }

    const srcDisplay = src ? `«<strong>${escapeHtml(src)}</strong>»` : '<span class="text-muted">[источник]</span>';
    const dstDisplay = dst ? `«<strong>${escapeHtml(dst)}</strong>»` : '<span class="text-muted">[назначение]</span>';
    const portDisplay = port ? `порт <strong>${escapeHtml(port)}</strong>` : '<span class="text-muted">[порт]</span>';
    const ttlDisplay = ttl ? `на <strong>${escapeHtml(ttl)} мин.</strong>` : '<span class="text-muted">[срок]</span>';

    linkDirectionSummary.className = 'direction-summary-text';
    linkDirectionSummary.innerHTML = `Открытие доступа: с узла ${srcDisplay} к узлу ${dstDisplay}, протокол <strong>${escapeHtml(proto)}</strong>, ${portDisplay}, ${ttlDisplay}.`;
  }

  async function handleCreateLinkSubmit(e) {
    e.preventDefault();

    const sourceNodeId = (linkSourceNode.value || '').trim();
    const targetNodeId = (linkTargetNode.value || '').trim();
    const protocol = (linkProtocol.value || 'tcp').trim().toLowerCase();
    const portStr = (linkPort.value || '').trim();
    const ttlStr = (linkTtl.value || '').trim();
    const reason = (linkReason.value || '').trim();

    if (!sourceNodeId || !/^[a-z0-9-]{1,63}$/.test(sourceNodeId)) {
      showLinkModalError('Укажите корректный Source Node ID (1-63 латинских букв, цифр или дефисов).');
      linkSourceNode.focus();
      return;
    }

    if (!targetNodeId || !/^[a-z0-9-]{1,63}$/.test(targetNodeId)) {
      showLinkModalError('Укажите корректный Target Node ID (1-63 латинских букв, цифр или дефисов).');
      linkTargetNode.focus();
      return;
    }

    if (sourceNodeId === targetNodeId) {
      showLinkModalError('Узел-источник и узел-назначение не могут совпадать.');
      linkTargetNode.focus();
      return;
    }

    const port = parseInt(portStr, 10);
    if (isNaN(port) || port < 1 || port > 65535) {
      showLinkModalError('Порт должен быть целым числом в диапазоне от 1 до 65535.');
      linkPort.focus();
      return;
    }

    const ttlMinutes = parseInt(ttlStr, 10);
    if (isNaN(ttlMinutes) || ttlMinutes < 0 || ttlMinutes > 525600) {
      showLinkModalError('Срок действия (TTL) должен быть числом минут от 0 до 525600.');
      linkTtl.focus();
      return;
    }

    if (!reason || reason.length === 0) {
      showLinkModalError('Причина открытия доступа (Reason) обязательна для аудита безопасности.');
      linkReason.focus();
      return;
    }

    if (reason.length > 256) {
      showLinkModalError('Причина не должна превышать 256 символов.');
      linkReason.focus();
      return;
    }

    if (!currentLinkCreationIdempotencyKey) {
      currentLinkCreationIdempotencyKey = generateUuid();
    }

    hideLinkModalError();
    linkModalSubmitBtn.disabled = true;
    linkModalSubmitBtn.querySelector('span').textContent = 'Создание...';

    const payload = {
      sourceNodeId,
      targetNodeId,
      protocol,
      port,
      ttlMinutes,
      reason,
      idempotencyKey: currentLinkCreationIdempotencyKey
    };

    try {
      const res = await authFetch('/api/v1/control/links', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(payload)
      });

      if (res.status === 401) {
        showLinkModalError('Требуется авторизация роли Operator.');
        return;
      }
      if (res.status === 403) {
        showLinkModalError('Доступ запрещен: роль не является Operator.');
        return;
      }

      if (!res.ok) {
        let errorMsg = `Ошибка создания Link (код ${res.status})`;
        try {
          const errorData = await res.json();
          if (errorData?.detail) {
            errorMsg = errorData.detail;
          } else if (errorData?.title) {
            errorMsg = errorData.title;
          } else if (errorData?.errors) {
            errorMsg = Object.values(errorData.errors).flat().join(' ');
          }
        } catch {
          const text = await res.text();
          if (text) errorMsg = text;
        }
        showLinkModalError(errorMsg);
        return;
      }

      // Success
      currentLinkCreationIdempotencyKey = null;
      closeCreateLinkModal();
      await loadDashboardData(true);
      showGlobalAlert(`Сетевая связь ${sourceNodeId} → ${targetNodeId}:${port} успешно создана.`, 'success');
    } catch (err) {
      console.error('Link creation error:', err);
      showLinkModalError(`Сетевая ошибка: ${err.message}`);
    } finally {
      linkModalSubmitBtn.disabled = false;
      linkModalSubmitBtn.querySelector('span').textContent = 'Подтвердить и создать Link';
    }
  }

  function showLinkModalError(msg) {
    linkModalError.textContent = msg;
    linkModalError.classList.remove('hidden');
  }

  function hideLinkModalError() {
    linkModalError.textContent = '';
    linkModalError.classList.add('hidden');
  }

  // Emergency Link Disabling
  async function handleLinksTableClick(e) {
    const disableBtn = e.target.closest('.btn-disable-link');
    if (!disableBtn) return;

    const linkId = disableBtn.getAttribute('data-link-id');
    const source = disableBtn.getAttribute('data-source');
    const target = disableBtn.getAttribute('data-target');
    const protocol = (disableBtn.getAttribute('data-protocol') || 'TCP').toUpperCase();
    const port = disableBtn.getAttribute('data-port');

    if (!linkId) return;

    const confirmed = window.confirm(
      `АВАРИЙНОЕ ОТКЛЮЧЕНИЕ СВЯЗИ:\n\nВы уверены, что хотите немедленно отозвать сетевой доступ с узла "${source}" к узлу "${target}" (${protocol}/${port})?`
    );

    if (!confirmed) return;

    disableBtn.disabled = true;
    disableBtn.textContent = 'Отключение...';

    const disablePayload = {
      idempotencyKey: generateUuid()
    };

    try {
      const res = await authFetch(`/api/v1/control/links/${encodeURIComponent(linkId)}/disable`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(disablePayload)
      });

      if (res.status === 401) {
        showGlobalAlert('Требуется авторизация роли Operator для отключения связи.', 'error');
        return;
      }
      if (res.status === 403) {
        showGlobalAlert('Доступ запрещен: роль не является Operator.', 'error');
        return;
      }

      if (!res.ok) {
        let errorMsg = `Ошибка при отключении Link (код ${res.status})`;
        try {
          const errorData = await res.json();
          if (errorData?.detail) errorMsg = errorData.detail;
          else if (errorData?.title) errorMsg = errorData.title;
        } catch {
          const text = await res.text();
          if (text) errorMsg = text;
        }
        showGlobalAlert(errorMsg, 'error');
        return;
      }

      await loadDashboardData(true);
      showGlobalAlert(`Сетевая связь ${source} → ${target}:${port} успешно отключена.`, 'success');
    } catch (err) {
      console.error('Disable link error:', err);
      showGlobalAlert(`Сетевая ошибка при отключении Link: ${err.message}`, 'error');
    } finally {
      disableBtn.disabled = false;
      disableBtn.textContent = 'Отключить';
    }
  }

  // Add Node Modal & Code Generation
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
      const res = await authFetch(`/api/v1/control/agents/${encodeURIComponent(nodeId)}/enrollment-code`, {
        method: 'POST'
      });

      if (res.status === 401) {
        showModalError('Требуется авторизация роли Operator.');
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
    addNodeModal.classList.remove('hidden');
    nodeIdInput.focus();
  }

  function closeAddNodeModal() {
    isModalOpen = false;
    addNodeModal.classList.add('hidden');
    if (countdownInterval) {
      clearInterval(countdownInterval);
      countdownInterval = null;
    }
    loadDashboardData();
  }

  // Password Login Modal
  function showLoginModal() {
    if (loginModal) {
      loginModal.classList.remove('hidden');
      if (loginUsername) loginUsername.focus();
    }
  }

  function hideLoginModal() {
    if (loginModal) {
      loginModal.classList.add('hidden');
      hideLoginError();
    }
  }

  function showLoginError(msg) {
    if (loginError) {
      loginError.textContent = msg;
      loginError.classList.remove('hidden');
    }
  }

  function hideLoginError() {
    if (loginError) {
      loginError.textContent = '';
      loginError.classList.add('hidden');
    }
  }

  async function handleLoginSubmit(e) {
    e.preventDefault();
    const username = (loginUsername.value || '').trim();
    const password = loginPassword.value || '';

    if (!username || !password) {
      showLoginError('Заполните логин и пароль.');
      return;
    }

    hideLoginError();
    loginSubmitBtn.disabled = true;
    loginSubmitBtn.querySelector('span').textContent = 'Вход...';

    try {
      const res = await fetch('/api/v1/auth/login', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json'
        },
        body: JSON.stringify({ username, password })
      });

      if (res.status === 401) {
        showLoginError('Неверный логин или пароль.');
        return;
      }

      if (res.status === 429) {
        showLoginError('Превышено количество попыток входа (лимит 5/мин). Подождите перед повторной попыткой.');
        return;
      }

      if (!res.ok) {
        showLoginError('Вход по паролю отключён на этом сервере.');
        return;
      }

      const data = await res.json();
      if (data?.token) {
        setStoredToken(data.token);
        hideLoginModal();
        await loadDashboardData(true);
      }
    } catch (err) {
      showLoginError(`Ошибка сети: ${err.message}`);
    } finally {
      loginSubmitBtn.disabled = false;
      loginSubmitBtn.querySelector('span').textContent = 'Войти';
    }
  }

  async function handleLogout() {
    try {
      await authFetch('/api/v1/auth/logout', { method: 'POST' });
    } catch {
      // Ignore network errors on logout
    }
    setStoredToken('');
    if (logoutBtn) logoutBtn.classList.add('hidden');
    if (connectionStatus) connectionStatus.textContent = 'Сессия завершена';
    if (isPasswordLoginEnabled) {
      showLoginModal();
    } else {
      window.location.reload();
    }
  }

  // Utilities
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

  function formatExpiration(isoString) {
    if (!isoString) return '<span class="text-muted">Бессрочно</span>';
    const date = new Date(isoString);
    if (isNaN(date.getTime())) return '<span class="text-muted">—</span>';

    const remainingSec = Math.floor((date.getTime() - Date.now()) / 1000);
    const dateStr = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    if (remainingSec <= 0) {
      return `<span class="text-danger" title="${date.toLocaleString()}">Истёк (${dateStr})</span>`;
    }
    if (remainingSec < 60) {
      return `<span class="text-warning">через ${remainingSec} сек.</span>`;
    }
    if (remainingSec < 3600) {
      return `через ${Math.floor(remainingSec / 60)} мин. (${dateStr})`;
    }
    if (remainingSec < 86400) {
      return `через ${Math.floor(remainingSec / 3600)} ч. (${dateStr})`;
    }

    return `${date.toLocaleDateString()} ${dateStr}`;
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
