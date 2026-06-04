define(['emby-button', 'emby-select', 'emby-input'], function () {
    'use strict';

    // ------------------------------------------------------------------ helpers
    function escapeHtml(str) {
        return String(str)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function toMMSS(seconds) {
        if (seconds == null) return '-';
        var s = Math.round(seconds);
        return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
    }

    function apiGet(path) {
        return window.ApiClient.getJSON(window.ApiClient.getUrl(path));
    }

    function apiPost(path, body) {
        return fetch(window.ApiClient.getUrl(path), {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-MediaBrowser-Token': window.ApiClient.accessToken()
            },
            body: body !== undefined ? JSON.stringify(body) : undefined
        }).then(function (r) { return r.json(); });
    }

    function apiDelete(path) {
        return fetch(window.ApiClient.getUrl(path), {
            method: 'DELETE',
            headers: { 'X-MediaBrowser-Token': window.ApiClient.accessToken() }
        });
    }

    // ================================================================== MAIN MODULE
    return function (view) {

        // Shared state
        var introJobId    = null;
        var modalListenersAttached = false;
        var introPoll     = null;
        var currentSeriesId = null;
        var currentSeasonId = null;
        var mediaJobId       = null;
        var mediaPoll        = null;
        var mediaLiveRendered = {};
        var mediaStatsTick   = 0;
        var mergeJobId       = null;
        var mergePoll        = null;
        var mergeLiveRendered = {};

        // Queue state
        var queueItems        = [];   // { seriesId, seasonId, seriesName, seasonName, status }
        var queueRunning      = false;
        var currentQueueIndex = -1;
        var allEpisodeResults = [];   // { item, results:{}, status }

        // ============================================================= TAB SWITCHING
        view.querySelectorAll('.sc-main-tab-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var target = btn.getAttribute('data-sc-main');

                view.querySelectorAll('.sc-main-tab-btn').forEach(function (b) {
                    b.classList.toggle('active', b === btn);
                });
                view.querySelector('#scMainIntro').style.display = target === 'intro' ? '' : 'none';
                view.querySelector('#scMainMedia').style.display = target === 'media' ? '' : 'none';
                view.querySelector('#scMainMerge').style.display = target === 'merge' ? '' : 'none';

                if (target === 'media') { loadMediaStats(); loadMediaInfoSettings(); loadMediaLastRun(); }
                if (target === 'merge') loadMergeSettings();
            });
        });

        // ============================================================= INTRO DETECTION

        // ---- series / seasons ----
        function loadSeries() {
            var sel = view.querySelector('#selectSeries');
            sel.innerHTML = '<option value="">- Loading series... -</option>';
            apiGet('strmcompanion/series')
                .then(function (series) {
                    sel.innerHTML = '<option value="">- Select a series -</option>';
                    (series || []).forEach(function (s) {
                        var opt = document.createElement('option');
                        opt.value = s.Id;
                        opt.textContent = s.Name;
                        sel.appendChild(opt);
                    });
                })
                .catch(function (err) {
                    console.error('StrmCompanion: series error', err);
                    sel.innerHTML = '<option value="">Error: could not load series</option>';
                });
        }

        function loadSeasons(seriesId) {
            var selSeason = view.querySelector('#selectSeason');
            selSeason.disabled = true;
            view.querySelector('#btnAddToQueue').disabled = true;
            selSeason.innerHTML = '<option value="">- Loading seasons... -</option>';
            hideMarkers();

            apiGet('strmcompanion/series/' + seriesId + '/seasons')
                .then(function (seasons) {
                    selSeason.innerHTML = '<option value="">All seasons</option>';
                    (seasons || []).forEach(function (s) {
                        var opt = document.createElement('option');
                        opt.value = s.Id;
                        opt.textContent = s.Name;
                        selSeason.appendChild(opt);
                    });
                    selSeason.disabled = false;
                    view.querySelector('#btnAddToQueue').disabled = false;
                })
                .catch(function (err) {
                    console.error('StrmCompanion: seasons error', err);
                    selSeason.innerHTML = '<option value="">Error loading seasons</option>';
                });
        }

        // ---- markers ----
        function hideMarkers() {
            view.querySelector('#markersExpandWrap').style.display = 'none';
            currentSeasonId = null;
        }

        function loadMarkers(id, seriesView) {
            if (seriesView) {
                currentSeasonId = null;
            } else {
                currentSeasonId = id;
            }
            var wrap    = view.querySelector('#markersExpandWrap');
            var hdr     = view.querySelector('#markersHeader');
            var expBody = view.querySelector('#markersExpandBody');
            var loading   = view.querySelector('#markersLoading');
            var table     = view.querySelector('#markersTable');
            var tbody     = view.querySelector('#markersBody');
            var colSeason = view.querySelector('#colSeason');
            var btnDelSeasonMarkers = view.querySelector('#btnDeleteSeasonMarkers');
            var btnDelSeasonAll     = view.querySelector('#btnDeleteSeasonAll');

            btnDelSeasonMarkers.style.display = seriesView ? 'none' : '';
            btnDelSeasonAll.style.display     = seriesView ? 'none' : '';

            wrap.style.display = '';
            if (!hdr.classList.contains('sc-open')) {
                hdr.classList.add('sc-open');
                expBody.style.display = '';
            }
            loading.style.display = '';
            loading.textContent = 'Loading markers...';
            table.style.display = 'none';
            tbody.innerHTML = '';
            colSeason.style.display = seriesView ? '' : 'none';

            var path = seriesView
                ? 'strmcompanion/intro/markers/series/' + id
                : 'strmcompanion/intro/markers/season/' + id;

            apiGet(path)
                .then(function (episodes) {
                    loading.style.display = 'none';
                    if (!episodes || episodes.length === 0) {
                        loading.textContent = seriesView ? 'No episodes found.' : 'No episodes found in this season.';
                        loading.style.display = '';
                        return;
                    }
                    episodes.forEach(function (ep) {
                        var tr  = document.createElement('tr');
                        var len = ep.HasMarkers ? toMMSS(ep.IntroEndSeconds - ep.IntroStartSeconds) : '-';
                        var delBtn = ep.HasMarkers
                            ? '<button class="sc-btn-delete-ep emby-button sc-btn-danger" data-epid="' + ep.EpisodeId + '">Delete</button>'
                            : '';
                        var seasonCell = seriesView ? '<td>' + escapeHtml(ep.SeasonName || '') + '</td>' : '';
                        tr.innerHTML =
                            seasonCell +
                            '<td>' + (ep.EpisodeIndex != null ? ep.EpisodeIndex : '-') + '</td>' +
                            '<td>' + escapeHtml(ep.EpisodeName || '') + '</td>' +
                            '<td>' + (ep.HasMarkers ? toMMSS(ep.IntroStartSeconds) : '-') + '</td>' +
                            '<td>' + (ep.HasMarkers ? toMMSS(ep.IntroEndSeconds)   : '-') + '</td>' +
                            '<td>' + len + '</td>' +
                            '<td>' + (ep.HasMarkers
                                ? '<span class="sc-badge-green">Set</span>'
                                : '<span class="sc-badge-none">None</span>') + '</td>' +
                            '<td>' + delBtn + '</td>';
                        tbody.appendChild(tr);
                    });
                    table.style.display = '';
                })
                .catch(function (err) {
                    console.error('StrmCompanion: markers error', err);
                    loading.textContent = 'Could not load markers.';
                    loading.style.display = '';
                });
        }

        function reloadCurrentMarkers() {
            var seasonId = view.querySelector('#selectSeason').value;
            if (seasonId) {
                loadMarkers(seasonId, false);
            } else if (currentSeriesId) {
                loadMarkers(currentSeriesId, true);
            }
        }

        function loadFingerprintDatabase() {
            var loading = view.querySelector('#fpDbLoading');
            var empty   = view.querySelector('#fpDbEmpty');
            var table   = view.querySelector('#fpDbTable');
            var tbody   = view.querySelector('#fpDbTbody');

            loading.style.display = '';
            empty.style.display   = 'none';
            table.style.display   = 'none';
            tbody.innerHTML       = '';

            apiGet('strmcompanion/fingerprints')
                .then(function (rows) {
                    loading.style.display = 'none';
                    if (!rows || rows.length === 0) {
                        empty.style.display = '';
                        return;
                    }
                    rows.forEach(function (row) {
                        var hasSeasons = row.Seasons && row.Seasons.length > 0;
                        var uid = 'fpdb-' + row.SeriesId;

                        var seriesTr = document.createElement('tr');
                        seriesTr.innerHTML =
                            '<td>' +
                                (hasSeasons
                                    ? '<button class="sc-fpdb-toggle emby-button" data-target="' + uid + '" style="padding:2px 4px;margin-right:4px;min-width:0;background:none;border:none;cursor:pointer;vertical-align:middle;"><i class="md-icon sc-fpdb-chevron" style="font-size:18px;">chevron_right</i></button>'
                                    : '<span style="display:inline-block;width:26px;"></span>') +
                                escapeHtml(row.SeriesName) +
                            '</td>' +
                            '<td style="text-align:center;">' + row.EpisodeCount + '</td>' +
                            '<td style="text-align:right;white-space:nowrap;">' +
                                '<button class="sc-fpdb-del-fp emby-button sc-btn-danger" data-sid="' + row.SeriesId + '" style="margin-right:4px;">Delete fingerprints</button>' +
                                '<button class="sc-fpdb-del-db emby-button sc-btn-danger" data-sid="' + row.SeriesId + '" style="margin-right:4px;">Delete from database</button>' +
                                '<button class="sc-fpdb-del-all emby-button sc-btn-danger" data-sid="' + row.SeriesId + '">Delete all</button>' +
                            '</td>';
                        tbody.appendChild(seriesTr);

                        if (hasSeasons) {
                            row.Seasons.forEach(function (season) {
                                var seasonTr = document.createElement('tr');
                                seasonTr.classList.add('sc-fpdb-season-row');
                                seasonTr.setAttribute('data-parent', uid);
                                seasonTr.style.display = 'none';
                                seasonTr.innerHTML =
                                    '<td style="padding-left:36px;font-size:13px;color:#aaa;">' + escapeHtml(season.SeasonName) + '</td>' +
                                    '<td style="text-align:center;font-size:13px;color:#aaa;">' + season.EpisodeCount + '</td>' +
                                    '<td style="text-align:right;white-space:nowrap;">' +
                                        '<button class="sc-fpdb-del-fp emby-button sc-btn-danger" data-seasonid="' + season.SeasonId + '" style="margin-right:4px;">Delete fingerprints</button>' +
                                        '<button class="sc-fpdb-del-db emby-button sc-btn-danger" data-seasonid="' + season.SeasonId + '" style="margin-right:4px;">Delete from database</button>' +
                                        '<button class="sc-fpdb-del-all emby-button sc-btn-danger" data-seasonid="' + season.SeasonId + '">Delete all</button>' +
                                    '</td>';
                                tbody.appendChild(seasonTr);
                            });
                        }
                    });
                    table.style.display = '';
                })
                .catch(function (err) {
                    console.error('StrmCompanion: fingerprint database load error', err);
                    loading.textContent = 'Could not load fingerprint database.';
                    loading.style.display = '';
                });
        }

        function confirmDelete(msg, path, afterFn) {
            if (!confirm(msg)) return;
            apiDelete(path)
                .then(afterFn)
                .catch(function (err) {
                    console.error('StrmCompanion: delete error', err);
                    window.Dashboard.alert('Could not delete.');
                });
        }

        // ---- queue management ----
        function renderQueue() {
            var tbody   = view.querySelector('#queueBody');
            var table   = view.querySelector('#queueTable');
            var empty   = view.querySelector('#queueEmpty');
            var btnRun  = view.querySelector('#btnRunQueue');
            var btnClear = view.querySelector('#btnClearQueue');

            if (queueItems.length === 0) {
                table.style.display = 'none';
                empty.style.display = '';
                btnRun.disabled  = true;
                btnClear.disabled = true;
                return;
            }

            var hasPending = queueItems.some(function (it) { return it.status === 'pending'; });
            table.style.display  = '';
            empty.style.display  = 'none';
            btnClear.disabled    = queueRunning;
            btnRun.disabled      = queueRunning || !hasPending;

            tbody.innerHTML = '';
            queueItems.forEach(function (item, idx) {
                var tr = document.createElement('tr');
                if (item.status === 'running') tr.classList.add('sc-queue-running');

                var badge;
                if (item.status === 'pending') {
                    badge = '<span class="sc-badge-none">In queue</span>';
                } else if (item.status === 'running') {
                    badge = '<span class="sc-badge-ok" style="display:inline-flex;align-items:center;gap:5px;">' +
                            '<span class="sc-spinner" style="width:10px;height:10px;border-width:2px;flex-shrink:0;"></span>Running</span>';
                } else if (item.status === 'done') {
                    badge = '<span class="sc-badge-green">Complete</span>';
                } else if (item.status === 'failed') {
                    badge = '<span class="sc-badge-err">Failed</span>';
                } else {
                    badge = '<span class="sc-badge-none">Cancelled</span>';
                }

                var removeBtn = (item.status === 'pending' && !queueRunning)
                    ? '<button class="sc-queue-remove emby-button sc-btn-danger" data-idx="' + idx + '" style="font-size:11px;padding:2px 8px;white-space:nowrap;">Delete from queue</button>'
                    : '';

                tr.innerHTML =
                    '<td>' + (idx + 1) + '</td>' +
                    '<td>' + escapeHtml(item.seriesName) + '</td>' +
                    '<td>' + escapeHtml(item.seasonName || 'All seasons') + '</td>' +
                    '<td>' + badge + '</td>' +
                    '<td>' + removeBtn + '</td>';
                tbody.appendChild(tr);
            });
        }

        function addToQueue() {
            var seriesSel = view.querySelector('#selectSeries');
            var seasonSel = view.querySelector('#selectSeason');
            var seriesId = parseInt(seriesSel.value, 10);
            if (!seriesId) return;
            var seasonId   = seasonSel.value ? parseInt(seasonSel.value, 10) : null;
            var seriesName = seriesSel.options[seriesSel.selectedIndex].textContent;
            var seasonName = seasonId ? seasonSel.options[seasonSel.selectedIndex].textContent : null;

            var duplicate = queueItems.some(function (it) {
                return it.seriesId === seriesId && it.seasonId === seasonId && it.status === 'pending';
            });
            if (duplicate) return;

            queueItems.push({ seriesId: seriesId, seasonId: seasonId, seriesName: seriesName, seasonName: seasonName, status: 'pending' });
            renderQueue();
        }

        function removeFromQueue(idx) {
            if (queueRunning) return;
            queueItems.splice(idx, 1);
            renderQueue();
        }

        function clearQueue() {
            if (queueRunning) return;
            queueItems = [];
            renderQueue();
        }

        function runQueue() {
            var hasPending = queueItems.some(function (it) { return it.status === 'pending'; });
            if (!hasPending || queueRunning) return;
            queueRunning      = true;
            currentQueueIndex = 0;
            allEpisodeResults = [];
            processNextQueueItem();
        }

        function processNextQueueItem() {
            while (currentQueueIndex < queueItems.length &&
                   queueItems[currentQueueIndex].status !== 'pending') {
                currentQueueIndex++;
            }

            if (currentQueueIndex >= queueItems.length) {
                queueRunning = false;
                renderQueue();
                showQueueResults();
                return;
            }

            var item = queueItems[currentQueueIndex];
            item.status = 'running';
            renderQueue();
            showQueueProgress(item, currentQueueIndex + 1, queueItems.length);

            var body = { SeriesId: item.seriesId };
            if (item.seasonId) body.SeasonId = item.seasonId;

            apiPost('strmcompanion/intro/run', body)
                .then(function (resp) {
                    introJobId = resp.JobId;
                    startIntroPoll();
                })
                .catch(function (err) {
                    console.error('StrmCompanion: run failed', err);
                    item.status = 'failed';
                    allEpisodeResults.push({ item: item, results: {}, status: 'failed' });
                    renderQueue();
                    if (queueRunning) {
                        currentQueueIndex++;
                        processNextQueueItem();
                    } else {
                        showQueueResults();
                    }
                });
        }

        function showQueueView() {
            view.querySelector('#queueAddForm').style.display         = '';
            view.querySelector('#queueSection').style.display          = '';
            view.querySelector('#introProgressSection').style.display  = 'none';
            view.querySelector('#introResultsSection').style.display   = 'none';
            stopIntroPoll();
        }

        function showQueueProgress(item, pos, total) {
            view.querySelector('#queueAddForm').style.display         = 'none';
            view.querySelector('#introProgressSection').style.display  = '';
            view.querySelector('#introResultsSection').style.display   = 'none';

            var label = item.seriesName + ' · ' + (item.seasonName || 'All seasons');
            view.querySelector('#introProgressTitle').textContent    = 'Processing (' + pos + ' / ' + total + ')';
            view.querySelector('#introProgressSubtitle').textContent = label;
            view.querySelector('#introProgressBar').style.width      = '0%';
            view.querySelector('#introStatusText').textContent       = 'Preparing...';
        }

        function showQueueResults() {
            view.querySelector('#queueAddForm').style.display         = 'none';
            view.querySelector('#introProgressSection').style.display  = 'none';
            view.querySelector('#introResultsSection').style.display   = '';

            var titleEl   = view.querySelector('#introResultTitle');
            var summaryEl = view.querySelector('#introResultSummary');
            var tbody     = view.querySelector('#introResultRows');

            var doneCount   = allEpisodeResults.filter(function (r) { return r.status === 'done'; }).length;
            var failedCount = allEpisodeResults.filter(function (r) { return r.status === 'failed'; }).length;
            var cancCount   = allEpisodeResults.filter(function (r) { return r.status === 'cancelled'; }).length;

            if (doneCount > 0) {
                titleEl.textContent = 'Done!';
                titleEl.style.color = '#00a4dc';
            } else if (cancCount > 0 && failedCount === 0) {
                titleEl.textContent = 'Cancelled';
                titleEl.style.color = '#aaa';
            } else {
                titleEl.textContent = 'Failed';
                titleEl.style.color = '#cc0000';
            }

            var parts = [];
            if (doneCount > 0)   parts.push(doneCount + ' completed');
            if (failedCount > 0) parts.push(failedCount + ' failed');
            if (cancCount > 0)   parts.push(cancCount + ' cancelled');
            summaryEl.textContent = parts.join(', ') + '.';

            tbody.innerHTML = '';
            allEpisodeResults.forEach(function (r) {
                var seriesLabel = r.item.seriesName + ' · ' + (r.item.seasonName || 'All seasons');
                var results = r.results || {};
                var keys = Object.keys(results);
                if (keys.length === 0) {
                    var tr = document.createElement('tr');
                    var statusMsg = r.status === 'cancelled' ? 'Cancelled' : r.status === 'failed' ? 'Failed' : '';
                    tr.innerHTML =
                        '<td>' + escapeHtml(seriesLabel) + '</td>' +
                        '<td>–</td>' +
                        '<td><span class="sc-badge-none">' + escapeHtml(statusMsg) + '</span></td>';
                    tbody.appendChild(tr);
                } else {
                    keys.forEach(function (epId) {
                        var msg = results[epId] || '';
                        var isError = msg === 'Fingerprinting failed' || msg === 'No intro found' || msg.indexOf('failed') !== -1;
                        var tr = document.createElement('tr');
                        tr.innerHTML =
                            '<td>' + escapeHtml(seriesLabel) + '</td>' +
                            '<td>' + escapeHtml(String(epId)) + '</td>' +
                            '<td><span class="' + (isError ? 'sc-badge-none' : 'sc-badge-green') + '">' + escapeHtml(msg) + '</span></td>';
                        tbody.appendChild(tr);
                    });
                }
            });
        }

        // ---- intro job polling ----
        function startIntroPoll() {
            stopIntroPoll();
            introPoll = setInterval(pollIntroJob, 2000);
        }
        function stopIntroPoll() {
            if (introPoll) { clearInterval(introPoll); introPoll = null; }
        }
        function pollIntroJob() {
            if (!introJobId) return;
            apiGet('strmcompanion/intro/job/' + introJobId)
                .then(function (job) {
                    view.querySelector('#introProgressBar').style.width = (job.ProgressPercent || 0) + '%';
                    view.querySelector('#introStatusText').textContent   = job.CurrentActivity || job.Status || '';
                    if (job.Status === 'Completed' || job.Status === 'Failed' || job.Status === 'Cancelled') {
                        stopIntroPoll();
                        var item = queueItems[currentQueueIndex];
                        if (item) {
                            item.status = job.Status === 'Completed' ? 'done' :
                                          job.Status === 'Failed'    ? 'failed' : 'cancelled';
                            allEpisodeResults.push({ item: item, results: job.EpisodeResults || {}, status: item.status });
                        }
                        if (job.Status === 'Cancelled' || !queueRunning) {
                            queueRunning = false;
                            renderQueue();
                            showQueueResults();
                        } else {
                            currentQueueIndex++;
                            processNextQueueItem();
                        }
                    }
                })
                .catch(function () { stopIntroPoll(); });
        }

        // ---- intro event wiring ----
        view.querySelector('#selectSeries').addEventListener('change', function () {
            currentSeriesId = this.value || null;
            if (!currentSeriesId) {
                hideMarkers();
                var sel = view.querySelector('#selectSeason');
                sel.innerHTML = '<option value="">- Select a series first -</option>';
                sel.disabled = true;
                view.querySelector('#btnAddToQueue').disabled = true;
                return;
            }
            loadSeasons(currentSeriesId);
            loadMarkers(currentSeriesId, true);
        });

        view.querySelector('#selectSeason').addEventListener('change', function () {
            if (this.value) {
                loadMarkers(this.value, false);
            } else if (currentSeriesId) {
                loadMarkers(currentSeriesId, true);
            }
        });

        view.querySelector('#markersBody').addEventListener('click', function (e) {
            var btn = e.target.closest('.sc-btn-delete-ep');
            if (!btn) return;
            confirmDelete('Delete the intro marker for this episode?',
                'strmcompanion/intro/markers/episode/' + btn.getAttribute('data-epid'),
                reloadCurrentMarkers);
        });

        view.querySelector('#btnDeleteSeasonMarkers').addEventListener('click', function () {
            if (!currentSeasonId) return;
            confirmDelete('Delete ALL intro markers for this season?',
                'strmcompanion/intro/markers/season/' + currentSeasonId, reloadCurrentMarkers);
        });

        view.querySelector('#btnDeleteSeasonAll').addEventListener('click', function () {
            if (!currentSeasonId) return;
            confirmDelete('Clear the fingerprint cache for this season? Intro markers will be kept.',
                'strmcompanion/fingerprints/season/' + currentSeasonId, reloadCurrentMarkers);
        });

        view.querySelector('#btnDeleteSeriesMarkers').addEventListener('click', function () {
            if (!currentSeriesId) return;
            confirmDelete('Delete ALL intro markers for the entire series?',
                'strmcompanion/intro/markers/series/' + currentSeriesId, reloadCurrentMarkers);
        });

        view.querySelector('#btnDeleteSeriesAll').addEventListener('click', function () {
            if (!currentSeriesId) return;
            confirmDelete('Clear the fingerprint cache for the entire series? Intro markers will be kept.',
                'strmcompanion/fingerprints/series/' + currentSeriesId, reloadCurrentMarkers);
        });

        view.querySelector('#queueBody').addEventListener('click', function (e) {
            var btn = e.target.closest('.sc-queue-remove');
            if (!btn) return;
            removeFromQueue(parseInt(btn.getAttribute('data-idx'), 10));
        });

        view.querySelector('#fpDbTbody').addEventListener('click', function (e) {
            var toggle = e.target.closest('.sc-fpdb-toggle');
            if (toggle) {
                var target     = toggle.getAttribute('data-target');
                var seasonRows = view.querySelectorAll('.sc-fpdb-season-row[data-parent="' + target + '"]');
                var chevron    = toggle.querySelector('.sc-fpdb-chevron');
                var expanded   = seasonRows.length > 0 && seasonRows[0].style.display !== 'none';
                seasonRows.forEach(function (r) { r.style.display = expanded ? 'none' : ''; });
                if (chevron) chevron.textContent = expanded ? 'chevron_right' : 'expand_more';
                return;
            }

            var btn = e.target.closest('.sc-fpdb-del-fp, .sc-fpdb-del-db, .sc-fpdb-del-all');
            if (!btn) return;

            var sid      = btn.getAttribute('data-sid');
            var seasonId = btn.getAttribute('data-seasonid');
            var isAll    = btn.classList.contains('sc-fpdb-del-all');
            var isDb     = btn.classList.contains('sc-fpdb-del-db');

            var msg, path;
            if (seasonId) {
                if (isAll) {
                    msg  = 'Delete ALL intro markers AND fingerprint cache for this season?';
                    path = 'strmcompanion/intro/all/season/' + seasonId;
                } else if (isDb) {
                    msg  = 'Delete intro markers from the database for this season?';
                    path = 'strmcompanion/intro/markers/season/' + seasonId;
                } else {
                    msg  = 'Delete the fingerprint cache for this season?';
                    path = 'strmcompanion/fingerprints/season/' + seasonId;
                }
            } else {
                if (isAll) {
                    msg  = 'Delete ALL intro markers AND fingerprint cache for this series?';
                    path = 'strmcompanion/intro/all/series/' + sid;
                } else if (isDb) {
                    msg  = 'Delete intro markers from the database for this series?';
                    path = 'strmcompanion/intro/markers/series/' + sid;
                } else {
                    msg  = 'Delete the fingerprint cache for this series?';
                    path = 'strmcompanion/fingerprints/series/' + sid;
                }
            }
            confirmDelete(msg, path, loadFingerprintDatabase);
        });

        view.querySelector('#btnRefreshFpDb').addEventListener('click', loadFingerprintDatabase);

        view.querySelector('#btnAddToQueue').addEventListener('click', addToQueue);
        view.querySelector('#btnRunQueue').addEventListener('click', runQueue);
        view.querySelector('#btnClearQueue').addEventListener('click', clearQueue);

        view.querySelector('#btnCancel').addEventListener('click', function () {
            queueRunning = false;
            if (introJobId) apiDelete('strmcompanion/intro/job/' + introJobId);
            stopIntroPoll();
            var item = queueItems[currentQueueIndex];
            if (item && item.status === 'running') {
                item.status = 'cancelled';
                allEpisodeResults.push({ item: item, results: {}, status: 'cancelled' });
            }
            introJobId = null;
            renderQueue();
            showQueueResults();
        });

        view.querySelector('#btnBackToQueue').addEventListener('click', function () {
            introJobId = null;
            stopIntroPoll();
            view.querySelector('#introProgressBar').style.width = '0%';
            view.querySelector('#introStatusText').textContent  = 'Preparing...';
            showQueueView();
        });

        // ============================================================= MEDIA INFO

        function loadMediaStats() {
            var loading = view.querySelector('#statsLoading');
            var errEl   = view.querySelector('#statsError');

            loading.style.display = '';
            errEl.style.display   = 'none';

            apiGet('strmcompanion/mediainfo/stats')
                .then(function (stats) {
                    loading.style.display = 'none';

                    view.querySelector('#statMoviesTotal').textContent     = stats.TotalMovies    != null ? stats.TotalMovies    : '–';
                    view.querySelector('#statMoviesScanned').textContent   = stats.ScannedMovies  != null ? stats.ScannedMovies  : '–';
                    view.querySelector('#statEpisodesTotal').textContent   = stats.TotalEpisodes  != null ? stats.TotalEpisodes  : '–';
                    view.querySelector('#statEpisodesScanned').textContent = stats.ScannedEpisodes != null ? stats.ScannedEpisodes : '–';

                    var mp = view.querySelector('#statMoviesPending');
                    mp.textContent = stats.PendingMovies != null ? stats.PendingMovies : '–';
                    mp.classList.toggle('has-pending', stats.PendingMovies > 0);

                    var ep = view.querySelector('#statEpisodesPending');
                    ep.textContent = stats.PendingEpisodes != null ? stats.PendingEpisodes : '–';
                    ep.classList.toggle('has-pending', stats.PendingEpisodes > 0);
                })
                .catch(function (err) {
                    console.error('StrmCompanion MediaInfo: stats error', err);
                    loading.style.display = 'none';
                    errEl.style.display   = '';
                });
        }

        function startMediaScan() {
            mediaLiveRendered = {};
            mediaStatsTick    = 0;
            view.querySelector('#mediaHistoryRows').innerHTML         = '';
            view.querySelector('#mediaHistorySection').style.display  = 'none';
            view.querySelectorAll('[data-mfilter]').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-mfilter') === 'all'); });
            view.querySelector('#mediaScanControls').style.display    = 'none';
            view.querySelector('#mediaProgressSection').style.display = '';
            view.querySelector('#mediaResultsSection').style.display  = 'none';
            view.querySelector('#mediaStatusText').textContent        = 'Starting...';
            view.querySelector('#mediaProgressBar').style.width       = '0%';

            apiPost('strmcompanion/mediainfo/scan')
                .then(function (resp) { mediaJobId = resp.JobId; startMediaPoll(); })
                .catch(function (err) {
                    console.error('StrmCompanion MediaInfo: start scan error', err);
                    showMediaControls();
                });
        }

        function startMediaPoll() {
            stopMediaPoll();
            mediaPoll = setInterval(pollMediaJob, 2000);
        }
        function stopMediaPoll() {
            if (mediaPoll) { clearInterval(mediaPoll); mediaPoll = null; }
        }
        function updateMediaHistoryTable(job) {
            var results = job.EpisodeResults || {};
            var titles  = job.ItemTitles    || {};
            var tbody   = view.querySelector('#mediaHistoryRows');
            var section = view.querySelector('#mediaHistorySection');
            var scroll  = view.querySelector('#mediaHistoryScroll');
            var added   = false;

            Object.keys(results).forEach(function (id) {
                if (mediaLiveRendered[id]) return;
                mediaLiveRendered[id] = true;
                added = true;
                var msg   = results[id] || '';
                var title = titles[id]  || id;
                var isErr  = msg.toLowerCase().indexOf('error') === 0;
                var isNone = msg === 'No media info found';
                var badge  = isErr ? 'err' : isNone ? 'none' : 'green';
                var badgeCls = isErr ? 'sc-badge-err' : isNone ? 'sc-badge-none' : 'sc-badge-green';
                var tr = document.createElement('tr');
                tr.setAttribute('data-mbadge', badge);
                tr.innerHTML =
                    '<td>' + escapeHtml(title) + '</td>' +
                    '<td><span class="' + badgeCls + '">' + escapeHtml(msg) + '</span></td>';
                tbody.appendChild(tr);
            });

            if (added) {
                section.style.display = '';
                applyMediaHistoryFilter();
                scroll.scrollTop = scroll.scrollHeight;
            }
        }

        function applyMediaHistoryFilter() {
            var active = view.querySelector('[data-mfilter].active');
            var filter = active ? active.getAttribute('data-mfilter') : 'all';
            view.querySelectorAll('#mediaHistoryRows tr').forEach(function (tr) {
                var badge = tr.getAttribute('data-mbadge');
                tr.style.display = (filter === 'all' || badge === filter) ? '' : 'none';
            });
        }

        view.querySelectorAll('[data-mfilter]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                view.querySelectorAll('[data-mfilter]').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
                applyMediaHistoryFilter();
            });
        });

        function pollMediaJob() {
            if (!mediaJobId) return;
            apiGet('strmcompanion/mediainfo/job/' + mediaJobId)
                .then(function (job) {
                    view.querySelector('#mediaProgressBar').style.width = (job.ProgressPercent || 0) + '%';
                    view.querySelector('#mediaStatusText').textContent   = job.CurrentActivity || '';
                    updateMediaHistoryTable(job);
                    if (++mediaStatsTick % 5 === 0) loadMediaStats();
                    if (job.Status === 'Completed' || job.Status === 'Failed' || job.Status === 'Cancelled') {
                        stopMediaPoll();
                        showMediaResults(job);
                    }
                })
                .catch(function () { stopMediaPoll(); showMediaControls(); });
        }

        function showMediaResults(job) {
            view.querySelector('#mediaProgressSection').style.display = 'none';
            view.querySelector('#mediaResultsSection').style.display  = '';

            var titleEl   = view.querySelector('#mediaResultTitle');
            var summaryEl = view.querySelector('#mediaResultSummary');
            var count     = Object.keys(job.EpisodeResults || {}).length;

            if (job.Status === 'Completed') {
                titleEl.textContent = 'Scan complete';
                titleEl.style.color = '#52B54B';
                summaryEl.textContent = count + ' item(s) processed.';
            } else if (job.Status === 'Cancelled') {
                titleEl.textContent = 'Scan cancelled';
                titleEl.style.color = '#aaa';
                summaryEl.textContent = count > 0 ? count + ' item(s) processed before cancel.' : 'The scan was cancelled.';
            } else {
                titleEl.textContent = 'Scan failed';
                titleEl.style.color = '#cc0000';
                summaryEl.textContent = job.ErrorMessage || 'An error occurred.';
            }

            updateMediaHistoryTable(job);
            loadMediaStats();
        }

        function showMediaControls() {
            view.querySelector('#mediaScanControls').style.display    = '';
            view.querySelector('#mediaProgressSection').style.display = 'none';
            view.querySelector('#mediaResultsSection').style.display  = 'none';
            mediaJobId = null;
        }

        function loadMediaLastRun() {
            if (mediaJobId) return;
            if (Object.keys(mediaLiveRendered).length > 0) return;
            if (view.querySelector('#mediaResultsSection').style.display !== 'none') return;

            apiGet('strmcompanion/mediainfo/jobs')
                .then(function (jobs) {
                    // Reconnect to a running job
                    var running = (jobs || []).find(function (j) { return j.Status === 'Running'; });
                    if (running) {
                        mediaJobId = running.JobId;
                        view.querySelector('#mediaScanControls').style.display    = 'none';
                        view.querySelector('#mediaResultsSection').style.display  = 'none';
                        view.querySelector('#mediaProgressSection').style.display = '';
                        view.querySelector('#mediaStatusText').textContent        = running.CurrentActivity || 'Resuming...';
                        view.querySelector('#mediaProgressBar').style.width       = (running.ProgressPercent || 0) + '%';
                        startMediaPoll();
                        return null;
                    }
                    var finished = (jobs || []).filter(function (j) {
                        return j.Status === 'Completed' || j.Status === 'Cancelled' || j.Status === 'Failed';
                    });
                    if (finished.length === 0) return null;
                    return apiGet('strmcompanion/mediainfo/job/' + finished[0].JobId);
                })
                .then(function (job) {
                    if (!job || !job.EpisodeResults) return;
                    updateMediaHistoryTable(job);
                    var titleEl   = view.querySelector('#mediaResultTitle');
                    var summaryEl = view.querySelector('#mediaResultSummary');
                    var count     = Object.keys(job.EpisodeResults).length;
                    if (job.Status === 'Completed') {
                        titleEl.textContent   = 'Scan complete';
                        titleEl.style.color   = '#52B54B';
                        summaryEl.textContent = count + ' item(s) processed.';
                    } else if (job.Status === 'Cancelled') {
                        titleEl.textContent   = 'Scan cancelled';
                        titleEl.style.color   = '#aaa';
                        summaryEl.textContent = count > 0 ? count + ' item(s) processed before cancel.' : 'The scan was cancelled.';
                    } else {
                        titleEl.textContent   = 'Scan failed';
                        titleEl.style.color   = '#cc0000';
                        summaryEl.textContent = job.ErrorMessage || 'An error occurred.';
                    }
                    view.querySelector('#mediaScanControls').style.display    = 'none';
                    view.querySelector('#mediaProgressSection').style.display = 'none';
                    view.querySelector('#mediaResultsSection').style.display  = '';
                })
                .catch(function () {});
        }

        view.querySelector('#btnStartScan').addEventListener('click', startMediaScan);
        view.querySelector('#btnRefreshStats').addEventListener('click', loadMediaStats);
        view.querySelector('#btnMediaCancel').addEventListener('click', function () {
            if (mediaJobId) apiDelete('strmcompanion/mediainfo/job/' + mediaJobId);
            stopMediaPoll();
            showMediaControls();
            loadMediaStats();
        });
        view.querySelector('#btnMediaReset').addEventListener('click', function () {
            showMediaControls();
            loadMediaStats();
        });

        // ============================================================= SETTINGS

        function wireExpand(headerId, bodyId, onOpen) {
            var hdr  = view.querySelector(headerId);
            var body = view.querySelector(bodyId);
            if (!hdr || !body) return;
            hdr.addEventListener('click', function () {
                var open = hdr.classList.toggle('sc-open');
                body.style.display = open ? '' : 'none';
                if (open && onOpen) onOpen();
            });
        }
        wireExpand('#introSettingsHeader', '#introSettingsBody', loadIntroSettings);
        wireExpand('#mediaSettingsHeader', '#mediaSettingsBody', loadMediaInfoSettings);
        wireExpand('#mergeSettingsHeader', '#mergeSettingsBody');
        wireExpand('#markersHeader', '#markersExpandBody');
        wireExpand('#fpDbHeader', '#fpDbBody', loadFingerprintDatabase);

        function loadIntroSettings() {
            apiGet('strmcompanion/settings')
                .then(function (cfg) {
                    view.querySelector('#txtFingerprintPath').value    = cfg.FingerprintDataPath || '';
                    view.querySelector('#txtFfmpegPath').value         = cfg.FfmpegPathOverride || '';
                    view.querySelector('#numFingerprintMinutes').value  = cfg.FingerprintDurationMinutes;
                    view.querySelector('#numMinIntroLength').value      = cfg.MinimumIntroLengthSeconds;
                    view.querySelector('#numHamming').value             = cfg.HammingDistanceThreshold;
                    view.querySelector('#txtSilenceDb').value           = cfg.SilenceThresholdDb || '';
                    view.querySelector('#chkOverwriteMarkers').checked  = !!cfg.OverwriteExistingIntroMarkers;
                    var eff = view.querySelector('#lblEffectivePath');
                    if (eff) eff.textContent = 'Current path: ' + (cfg.EffectiveFingerprintPath || '(unknown)');
                })
                .catch(function (err) { console.error('StrmCompanion: intro settings load error', err); });
        }

        function loadMediaInfoSettings() {
            var warnEl = view.querySelector('#noLibrariesWarn');
            apiGet('strmcompanion/mediainfo/settings')
                .then(function (cfg) {
                    var hasLibs = cfg.MediaInfoLibraryIds && cfg.MediaInfoLibraryIds.length > 0;
                    if (warnEl) warnEl.style.display = hasLibs ? 'none' : '';

                    view.querySelector('#numConcurrency').value = cfg.MediaInfoConcurrency || 2;
                    view.querySelector('#chkAutoScan').checked  = !!cfg.MediaInfoAutoScan;
                    var ids = (cfg.MediaInfoLibraryIds || '').split(',')
                        .map(function (s) { return s.trim(); }).filter(Boolean);
                    renderLibraries(ids);
                })
                .catch(function (err) { console.error('StrmCompanion: media info settings load error', err); });
        }

        function renderLibraries(selectedIds) {
            var container = view.querySelector('#libraryList');
            apiGet('strmcompanion/mediainfo/libraries')
                .then(function (libraries) {
                    container.innerHTML = '';
                    if (!libraries || libraries.length === 0) {
                        container.innerHTML = '<span style="color:#aaa;font-size:13px;">No libraries found.</span>';
                        return;
                    }
                    libraries.forEach(function (lib) {
                        var label = document.createElement('label');
                        label.className = 'sc-library-item';
                        var cb = document.createElement('input');
                        cb.type    = 'checkbox';
                        cb.value   = String(lib.Id);
                        cb.checked = selectedIds.indexOf(String(lib.Id)) !== -1;
                        var span = document.createElement('span');
                        span.textContent = lib.Name + (lib.CollectionType ? ' (' + lib.CollectionType + ')' : '');
                        label.appendChild(cb);
                        label.appendChild(span);
                        container.appendChild(label);
                    });
                })
                .catch(function () {
                    container.innerHTML = '<span style="color:#cc4444;font-size:13px;">Failed to load libraries.</span>';
                });
        }

        function saveIntroSettings() {
            var body = {
                FingerprintDataPath:           view.querySelector('#txtFingerprintPath').value.trim(),
                FfmpegPathOverride:             view.querySelector('#txtFfmpegPath').value.trim(),
                FingerprintDurationMinutes:     parseInt(view.querySelector('#numFingerprintMinutes').value, 10),
                MinimumIntroLengthSeconds:      parseInt(view.querySelector('#numMinIntroLength').value, 10),
                HammingDistanceThreshold:       parseInt(view.querySelector('#numHamming').value, 10),
                SilenceThresholdDb:             view.querySelector('#txtSilenceDb').value.trim(),
                SilenceDurationSeconds:         0.5,
                OverwriteExistingIntroMarkers:  view.querySelector('#chkOverwriteMarkers').checked
            };
            apiPost('strmcompanion/settings', body)
                .then(function (cfg) {
                    var eff = view.querySelector('#lblEffectivePath');
                    if (eff && cfg) eff.textContent = 'Current path: ' + (cfg.EffectiveFingerprintPath || '(unknown)');
                    var msg = view.querySelector('#introSettingsSavedMsg');
                    msg.style.display = '';
                    setTimeout(function () { msg.style.display = 'none'; }, 2500);
                })
                .catch(function (err) {
                    console.error('StrmCompanion: save intro settings error', err);
                    window.Dashboard.alert('Could not save settings.');
                });
        }

        function saveMediaSettings() {
            var checkboxes = view.querySelectorAll('#libraryList input[type=checkbox]:checked');
            var selectedIds = Array.prototype.slice.call(checkboxes).map(function (cb) { return cb.value; }).join(',');
            var body = {
                MediaInfoLibraryIds:  selectedIds,
                MediaInfoConcurrency: parseInt(view.querySelector('#numConcurrency').value, 10) || 2,
                MediaInfoAutoScan:    view.querySelector('#chkAutoScan').checked
            };
            apiPost('strmcompanion/mediainfo/settings', body)
                .then(function () {
                    var warnEl = view.querySelector('#noLibrariesWarn');
                    if (warnEl) warnEl.style.display = selectedIds ? 'none' : '';
                    var msg = view.querySelector('#mediaSettingsSavedMsg');
                    msg.style.display = '';
                    setTimeout(function () { msg.style.display = 'none'; }, 2500);
                })
                .catch(function (err) {
                    console.error('StrmCompanion: save media settings error', err);
                    window.Dashboard.alert('Could not save settings.');
                });
        }

        view.querySelector('#btnSaveIntroSettings').addEventListener('click', saveIntroSettings);
        view.querySelector('#btnSaveMediaSettings').addEventListener('click', saveMediaSettings);

        // ============================================================= MERGE VERSION

        function loadMergeLastRun() {
            apiGet('strmcompanion/mergeversion/jobs')
                .then(function (jobs) {
                    // Reconnect to a running job
                    var running = (jobs || []).find(function (j) { return j.Status === 'Running'; });
                    if (running && !mergeJobId && Object.keys(mergeLiveRendered).length === 0) {
                        mergeJobId = running.JobId;
                        view.querySelector('#mergeControls').style.display        = 'none';
                        view.querySelector('#mergeResultsSection').style.display  = 'none';
                        view.querySelector('#mergeProgressSection').style.display = '';
                        view.querySelector('#mergeStatusText').textContent        = running.CurrentActivity || 'Resuming...';
                        view.querySelector('#mergeProgressBar').style.width       = (running.ProgressPercent || 0) + '%';
                        startMergePoll();
                        return;
                    }

                    var finished = (jobs || []).filter(function (j) {
                        return j.Status === 'Completed' || j.Status === 'Cancelled' || j.Status === 'Failed';
                    });
                    if (finished.length === 0) return;

                    var job     = finished[0];
                    var results = job.EpisodeResults || {};
                    var keys    = Object.keys(results);
                    var found   = keys.length;
                    var merged  = keys.filter(function (k) { return (results[k] || '').indexOf('Merged') === 0; }).length;
                    var failed  = keys.filter(function (k) { return (results[k] || '').toLowerCase().indexOf('error') === 0; }).length;

                    view.querySelector('#mergeStatFound').textContent  = found;
                    view.querySelector('#mergeStatMerged').textContent = merged;

                    var failEl = view.querySelector('#mergeStatFailed');
                    failEl.textContent = failed;
                    failEl.classList.toggle('has-pending', failed > 0);

                    var ts = job.CompletedAt || job.StartedAt;
                    var d  = ts ? new Date(ts) : null;
                    var dateStr = d ? d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
                    var statusLabel = job.Status === 'Completed' ? 'Completed' : job.Status === 'Cancelled' ? 'Cancelled' : 'Failed';
                    view.querySelector('#mergeLastRunTime').textContent = dateStr ? dateStr + ' — ' + statusLabel : statusLabel;

                    view.querySelector('#mergeLastRun').style.display = '';

                    if (!mergeJobId && Object.keys(mergeLiveRendered).length === 0
                            && view.querySelector('#mergeResultsSection').style.display === 'none') {
                        restoreMergeResults(job.JobId);
                    }
                })
                .catch(function () {});
        }

        function restoreMergeResults(jobId) {
            apiGet('strmcompanion/mergeversion/job/' + jobId)
                .then(function (fullJob) {
                    if (!fullJob) return;
                    var titleEl   = view.querySelector('#mergeResultTitle');
                    var summaryEl = view.querySelector('#mergeResultSummary');
                    var tbody     = view.querySelector('#mergeResultRows');
                    var results   = fullJob.EpisodeResults || {};
                    var keys      = Object.keys(results);
                    var merged    = keys.filter(function (k) { return (results[k] || '').indexOf('Merged') === 0; }).length;
                    var failed    = keys.filter(function (k) { return (results[k] || '').toLowerCase().indexOf('error') === 0; }).length;

                    if (fullJob.Status === 'Completed') {
                        titleEl.textContent   = 'Merge complete';
                        titleEl.style.color   = '#52B54B';
                        summaryEl.textContent = keys.length === 0
                            ? 'No duplicates found — nothing to merge.'
                            : merged + ' item(s) merged' + (failed > 0 ? ', ' + failed + ' failed.' : '.');
                    } else if (fullJob.Status === 'Cancelled') {
                        titleEl.textContent   = 'Merge cancelled';
                        titleEl.style.color   = '#aaa';
                        summaryEl.textContent = merged > 0 ? merged + ' item(s) merged before cancel.' : 'Cancelled before any merges.';
                    } else {
                        titleEl.textContent   = 'Merge failed';
                        titleEl.style.color   = '#cc0000';
                        summaryEl.textContent = fullJob.ErrorMessage || 'An error occurred.';
                    }

                    var titles = fullJob.ItemTitles || {};
                    tbody.innerHTML = '';
                    keys.forEach(function (id) {
                        var msg     = results[id] || '';
                        var isError = msg.toLowerCase().indexOf('error') === 0;
                        var title   = titles[id] || id;
                        var tr = document.createElement('tr');
                        tr.innerHTML =
                            '<td>' + escapeHtml(title) + '</td>' +
                            '<td><span class="' + (isError ? 'sc-badge-err' : 'sc-badge-green') + '">' + escapeHtml(msg) + '</span></td>';
                        tbody.appendChild(tr);
                    });

                    view.querySelector('#mergeControls').style.display        = 'none';
                    view.querySelector('#mergeProgressSection').style.display = 'none';
                    view.querySelector('#mergeResultsSection').style.display  = '';
                })
                .catch(function () {});
        }

        function loadMergeSettings() {
            loadMergeLastRun();
            apiGet('strmcompanion/mergeversion/settings')
                .then(function (cfg) {
                    view.querySelector('#selectMergeMoviesScope').value  = cfg.MergeMoviesScope || 'GlobalScope';
                    view.querySelector('#selectMergeSeriesScope').value  = cfg.MergeSeriesScope || 'Disabled';
                    view.querySelector('#chkMergeAutoDetect').checked    = !!cfg.MergeAutoDetect;
                })
                .catch(function (err) {
                    console.error('StrmCompanion MergeVersion: load error', err);
                });
        }

        function saveMergeSettings() {
            var body = {
                MergeMoviesScope: view.querySelector('#selectMergeMoviesScope').value,
                MergeSeriesScope: view.querySelector('#selectMergeSeriesScope').value,
                MergeAutoDetect:  view.querySelector('#chkMergeAutoDetect').checked
            };
            apiPost('strmcompanion/mergeversion/settings', body)
                .then(function () {
                    var msg = view.querySelector('#mergeSavedMsg');
                    msg.style.display = '';
                    setTimeout(function () { msg.style.display = 'none'; }, 2500);
                })
                .catch(function (err) {
                    console.error('StrmCompanion MergeVersion: save error', err);
                    window.Dashboard.alert('Could not save settings.');
                });
        }

        function startMerge() {
            mergeLiveRendered = {};
            view.querySelector('#mergeScanLiveRows').innerHTML        = '';
            view.querySelector('#mergeScanLiveWrap').style.display    = 'none';
            view.querySelector('#mergeControls').style.display        = 'none';
            view.querySelector('#mergeProgressSection').style.display = '';
            view.querySelector('#mergeResultsSection').style.display  = 'none';
            view.querySelector('#mergeStatusText').textContent        = 'Starting...';
            view.querySelector('#mergeProgressBar').style.width       = '0%';

            apiPost('strmcompanion/mergeversion/run')
                .then(function (resp) {
                    mergeJobId = resp.JobId;
                    startMergePoll();
                })
                .catch(function (err) {
                    console.error('StrmCompanion MergeVersion: start error', err);
                    showMergeControls();
                });
        }

        function startMergePoll() {
            stopMergePoll();
            mergePoll = setInterval(pollMergeJob, 2000);
        }

        function stopMergePoll() {
            if (mergePoll) { clearInterval(mergePoll); mergePoll = null; }
        }

        function updateMergeLiveTable(job) {
            var results = job.EpisodeResults || {};
            var titles  = job.ItemTitles    || {};
            var tbody   = view.querySelector('#mergeScanLiveRows');
            var wrap    = view.querySelector('#mergeScanLiveWrap');
            var added   = false;

            Object.keys(results).forEach(function (id) {
                if (mergeLiveRendered[id]) return;
                mergeLiveRendered[id] = true;
                added = true;
                var msg   = results[id] || '';
                var title = titles[id]  || id;
                var isErr = msg.toLowerCase().indexOf('error') === 0;
                var badgeCls = isErr ? 'sc-badge-err' : 'sc-badge-green';
                var tr = document.createElement('tr');
                tr.innerHTML =
                    '<td>' + escapeHtml(title) + '</td>' +
                    '<td><span class="' + badgeCls + '">' + escapeHtml(msg) + '</span></td>';
                tbody.appendChild(tr);
            });

            if (added) {
                wrap.style.display = '';
                wrap.querySelector('div').scrollTop = wrap.querySelector('div').scrollHeight;
            }
        }

        function pollMergeJob() {
            if (!mergeJobId) return;
            apiGet('strmcompanion/mergeversion/job/' + mergeJobId)
                .then(function (job) {
                    view.querySelector('#mergeProgressBar').style.width = (job.ProgressPercent || 0) + '%';
                    view.querySelector('#mergeStatusText').textContent   = job.CurrentActivity || '';
                    updateMergeLiveTable(job);
                    if (job.Status === 'Completed' || job.Status === 'Failed' || job.Status === 'Cancelled') {
                        stopMergePoll();
                        showMergeResults(job);
                    }
                })
                .catch(function () { stopMergePoll(); showMergeControls(); });
        }

        function showMergeResults(job) {
            view.querySelector('#mergeProgressSection').style.display = 'none';
            view.querySelector('#mergeResultsSection').style.display  = '';

            var titleEl   = view.querySelector('#mergeResultTitle');
            var summaryEl = view.querySelector('#mergeResultSummary');
            var tbody     = view.querySelector('#mergeResultRows');
            var results   = job.EpisodeResults || {};
            var keys      = Object.keys(results);
            var merged    = keys.filter(function (k) { return (results[k] || '').indexOf('Merged') === 0; }).length;
            var failed    = keys.filter(function (k) { return (results[k] || '').toLowerCase().indexOf('error') === 0; }).length;

            if (job.Status === 'Completed') {
                titleEl.textContent   = 'Merge complete';
                titleEl.style.color   = '#52B54B';
                if (keys.length === 0) {
                    summaryEl.textContent = 'No duplicates found — nothing to merge.';
                } else {
                    summaryEl.textContent = merged + ' item(s) merged' + (failed > 0 ? ', ' + failed + ' failed.' : '.');
                }
            } else if (job.Status === 'Cancelled') {
                titleEl.textContent   = 'Merge cancelled';
                titleEl.style.color   = '#aaa';
                summaryEl.textContent = merged > 0 ? merged + ' item(s) merged before cancel.' : 'Cancelled before any merges.';
            } else {
                titleEl.textContent   = 'Merge failed';
                titleEl.style.color   = '#cc0000';
                summaryEl.textContent = job.ErrorMessage || 'An error occurred.';
            }

            var titles = job.ItemTitles || {};
            tbody.innerHTML = '';
            keys.forEach(function (id) {
                var msg     = results[id] || '';
                var isError = msg.toLowerCase().indexOf('error') === 0;
                var title   = titles[id] || id;
                var tr = document.createElement('tr');
                tr.innerHTML =
                    '<td>' + escapeHtml(title) + '</td>' +
                    '<td><span class="' + (isError ? 'sc-badge-err' : 'sc-badge-green') + '">' + escapeHtml(msg) + '</span></td>';
                tbody.appendChild(tr);
            });

            // Refresh the last-run stats card immediately
            loadMergeLastRun();
        }

        function showMergeControls() {
            view.querySelector('#mergeControls').style.display        = '';
            view.querySelector('#mergeProgressSection').style.display = 'none';
            view.querySelector('#mergeResultsSection').style.display  = 'none';
            mergeJobId = null;
        }

        view.querySelector('#btnSaveMergeSettings').addEventListener('click', saveMergeSettings);
        view.querySelector('#btnRunMerge').addEventListener('click', startMerge);

        view.querySelector('#btnMergeCancel').addEventListener('click', function () {
            if (mergeJobId) apiDelete('strmcompanion/mergeversion/job/' + mergeJobId);
            stopMergePoll();
            showMergeControls();
        });

        view.querySelector('#btnMergeReset').addEventListener('click', showMergeControls);

        // ============================================================= FOOTER
        function applyPluginTheme() {
            var candidates = ['.skinHeader', '.mainDrawer', '.contentScrollSlider', 'body'];
            var bg = null;
            for (var i = 0; i < candidates.length; i++) {
                var el = document.querySelector(candidates[i]);
                if (!el) continue;
                var c = getComputedStyle(el).backgroundColor;
                if (c && c !== 'transparent' && c !== 'rgba(0, 0, 0, 0)') { bg = c; break; }
            }
            var isDark = true;
            if (bg) {
                var m = bg.match(/\d+/g);
                if (m) isDark = (parseInt(m[0]) * 0.299 + parseInt(m[1]) * 0.587 + parseInt(m[2]) * 0.114) < 128;
            }
            var root = document.documentElement;
            if (isDark) {
                root.style.setProperty('--plugin-popup-bg',     '#2a2a2a');
                root.style.setProperty('--plugin-popup-bg2',    '#333333');
                root.style.setProperty('--plugin-popup-color',  '#e8e8e8');
                root.style.setProperty('--plugin-popup-muted',  '#aaaaaa');
                root.style.setProperty('--plugin-popup-border', 'rgba(255,255,255,0.12)');
                root.style.setProperty('--plugin-popup-hover',  'rgba(255,255,255,0.08)');
                root.style.setProperty('--plugin-popup-badge',  'rgba(255,255,255,0.1)');
                root.style.setProperty('--plugin-input-border', 'rgba(255,255,255,0.2)');
                root.style.setProperty('--plugin-input-bg',     'rgba(255,255,255,0.08)');
                root.style.setProperty('--plugin-footer-bg',    '#181818');
                root.dataset.pluginTheme = 'dark';
            } else {
                root.style.setProperty('--plugin-popup-bg',     '#f2f2f2');
                root.style.setProperty('--plugin-popup-bg2',    '#e0e0e0');
                root.style.setProperty('--plugin-popup-color',  '#1a1a1a');
                root.style.setProperty('--plugin-popup-muted',  '#555555');
                root.style.setProperty('--plugin-popup-border', 'rgba(0,0,0,0.15)');
                root.style.setProperty('--plugin-popup-hover',  'rgba(0,0,0,0.08)');
                root.style.setProperty('--plugin-popup-badge',  'rgba(0,0,0,0.1)');
                root.style.setProperty('--plugin-input-border', 'rgba(0,0,0,0.28)');
                root.style.setProperty('--plugin-input-bg',     'rgba(0,0,0,0.04)');
                root.style.setProperty('--plugin-footer-bg',    '#c5cad1');
                root.dataset.pluginTheme = 'light';
            }
            var drawer = document.querySelector('.mainDrawer');
            var drawerRight = drawer ? drawer.getBoundingClientRect().right : 0;
            var footerLeft = (drawerRight > 0 && drawerRight < window.innerWidth * 0.5) ? drawerRight : 0;
            root.style.setProperty('--plugin-footer-left', footerLeft + 'px');
        }

        function initFooter() {
            applyPluginTheme();

            apiGet('strmcompanion/version')
                .then(function (result) {
                    var ver = result.Version || '';
                    var footerVer = document.getElementById('footerVersionText');
                    if (footerVer && ver) {
                        var releaseUrl = 'https://github.com/soderlund91/StrmCompanion/releases/tag/v' + ver;
                        footerVer.innerHTML = '<a href="' + releaseUrl + '" target="_blank" style="color:inherit;text-decoration:none;">v' + ver + '</a>';
                    }
                    if (!ver) return;

                    fetch('https://api.github.com/repos/soderlund91/StrmCompanion/releases/latest')
                        .then(function (r) { return r.json(); })
                        .then(function (release) {
                            var latestTag = (release.tag_name || '').replace(/^v/i, '');
                            if (!latestTag) return;
                            var a = latestTag.split('.').map(Number);
                            var b = ver.split('.').map(Number);
                            var isNewer = false;
                            for (var i = 0; i < Math.max(a.length, b.length); i++) {
                                if ((a[i] || 0) > (b[i] || 0)) { isNewer = true; break; }
                                if ((a[i] || 0) < (b[i] || 0)) break;
                            }
                            if (isNewer) {
                                var footerUpdate = document.getElementById('footerUpdateInfo');
                                if (footerUpdate) {
                                    footerUpdate.innerHTML = '<a href="' + release.html_url + '" target="_blank" class="footer-update-link">Update available: v' + latestTag + '</a>';
                                    var footerUpdateSep = document.getElementById('footerUpdateSep');
                                    if (footerUpdateSep) footerUpdateSep.style.display = '';
                                }
                            }
                        })
                        .catch(function () {});
                })
                .catch(function () {});
        }

        // ============================================================= VIEWSHOW / VIEWHIDE
        view.addEventListener('viewshow', function () {
            initFooter();

            if (!modalListenersAttached) {
                modalListenersAttached = true;

                var helpOverlay = view.querySelector('#helpModalOverlay');
                var bugOverlay  = view.querySelector('#bugReportModalOverlay');
                var logOverlay  = view.querySelector('#logModalOverlay');

                view.querySelector('#btnOpenHelp').addEventListener('click', function () { helpOverlay.classList.add('modal-visible'); });
                view.querySelector('#btnCloseHelp').addEventListener('click', function () { helpOverlay.classList.remove('modal-visible'); });
                helpOverlay.addEventListener('click', function (e) { if (e.target === helpOverlay) helpOverlay.classList.remove('modal-visible'); });

                helpOverlay.querySelectorAll('.help-nav-btn').forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var target = btn.getAttribute('data-help');
                        helpOverlay.querySelectorAll('.help-nav-btn').forEach(function (b) { b.classList.remove('active'); });
                        helpOverlay.querySelectorAll('.help-page').forEach(function (p) { p.classList.remove('active'); });
                        btn.classList.add('active');
                        var page = helpOverlay.querySelector('#helpPage' + target.charAt(0).toUpperCase() + target.slice(1));
                        if (page) page.classList.add('active');
                    });
                });

                view.querySelector('#btnOpenBugReport').addEventListener('click', function () { bugOverlay.classList.add('modal-visible'); });
                view.querySelector('#btnCloseBugReport').addEventListener('click', function () { bugOverlay.classList.remove('modal-visible'); });
                bugOverlay.addEventListener('click', function (e) { if (e.target === bugOverlay) bugOverlay.classList.remove('modal-visible'); });

                view.querySelector('#btnOpenLogs').addEventListener('click', function () { logOverlay.classList.add('modal-visible'); });
                view.querySelector('#btnCloseLogs').addEventListener('click', function () { logOverlay.classList.remove('modal-visible'); });
                logOverlay.addEventListener('click', function (e) { if (e.target === logOverlay) logOverlay.classList.remove('modal-visible'); });
            }

            // Reset intro state
            introJobId = null;
            currentSeriesId = null;
            currentSeasonId = null;
            stopIntroPoll();
            queueRunning = false;
            queueItems.forEach(function (it) { if (it.status === 'running') it.status = 'pending'; });
            view.querySelector('#selectSeries').value = '';
            var selSeason = view.querySelector('#selectSeason');
            selSeason.disabled = true;
            selSeason.innerHTML = '<option value="">- Select a series first -</option>';
            view.querySelector('#btnAddToQueue').disabled = true;
            hideMarkers();
            showQueueView();
            renderQueue();
            loadSeries();

            // Always start on Intro Detection tab
            view.querySelectorAll('.sc-main-tab-btn').forEach(function (b) {
                b.classList.toggle('active', b.getAttribute('data-sc-main') === 'intro');
            });
            view.querySelector('#scMainIntro').style.display = '';
            view.querySelector('#scMainMedia').style.display = 'none';
            view.querySelector('#scMainMerge').style.display = 'none';
        });

        view.addEventListener('viewhide', function () {
            stopIntroPoll();
            stopMediaPoll();
            stopMergePoll();
        });
    };
});
