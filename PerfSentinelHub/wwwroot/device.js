// The terminal sign-in approval page, served by GET /device only. The code is always typed:
// nothing here reads location.search or location.hash, so no link can fill it in. The texts
// come from launcher.js, where the Node tests pin them.
(function () {
    'use strict';

    // The launcher's theme, resolved before the first paint: tokens.css has no palette
    // without the attribute. This page allows no inline script, so it lives here.
    let stored = null;
    try {
        stored = sessionStorage.getItem('perf-sentinel:theme') || localStorage.getItem('perf-sentinel:theme');
    } catch (e) {
        stored = null;
    }
    document.documentElement.dataset.theme = stored === 'light' || stored === 'dark'
        ? stored
        : matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';

    function send(path, body) {
        return fetch(path, {
            method: 'POST',
            headers: {'content-type': 'application/json'},
            body: JSON.stringify(body)
        }).then(function (response) {
            // A lapsed session signs in again, and so does one too close to its end (409).
            if (response.status === 401 || response.status === 409) location.reload();
            return response.json().catch(function () {
                return null;
            }).then(function (payload) {
                return {status: response.status, payload: payload};
            });
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        const form = document.getElementById('device-form');
        const input = document.getElementById('device-code');
        const details = document.getElementById('device-details');
        const networkAlert = document.getElementById('device-alert');
        const approve = document.getElementById('device-approve');
        const deny = document.getElementById('device-deny');
        const result = document.getElementById('device-result');
        let verified = null;
        let armed = false;

        function say(text, tone) {
            result.textContent = text;
            result.dataset.tone = tone;
            result.hidden = false;
        }

        function refused(answer) {
            say(answer.payload && answer.payload.detail || 'The Hub answered ' + answer.status + '.', 'crit');
        }

        fetch('/api/access', {headers: {accept: 'application/json'}}).then(function (response) {
            return response.ok ? response.json() : null;
        }).then(function (access) {
            if (access) document.getElementById('device-intro').textContent = PSL.deviceIntro(access.expires_at_ms);
        }).catch(function () {
            // The intro keeps its generic end.
        });

        form.addEventListener('submit', function (event) {
            event.preventDefault();
            result.hidden = true;
            details.hidden = true;
            send('/api/device/verify', {user_code: input.value}).then(function (answer) {
                if (answer.status !== 200) return refused(answer);
                verified = answer.payload;
                armed = false;
                approve.textContent = 'Approve';
                document.getElementById('device-request').textContent = PSL.deviceRequestLine(verified, Date.now());
                const warning = PSL.deviceNetworkAlert(verified);
                networkAlert.textContent = warning || '';
                networkAlert.hidden = !warning;
                details.hidden = false;
            });
        });

        function decide(approved) {
            send('/api/device/approve', {user_code: verified.user_code, approve: approved}).then(function (answer) {
                if (answer.status !== 204) return refused(answer);
                form.hidden = true;
                details.hidden = true;
                say(approved
                    ? 'Approved. Your terminal signs in within a few seconds; you can close this page.'
                    : 'Denied. That terminal gets no access.', approved ? 'ok' : 'muted');
            });
        }

        approve.addEventListener('click', function () {
            if (PSL.deviceApproveStep(verified.same_network, armed) === 'arm') {
                armed = true;
                approve.textContent = 'Approve anyway';
                return;
            }
            decide(true);
        });
        deny.addEventListener('click', function () {
            decide(false);
        });
    });
})();
