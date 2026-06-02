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
        var mediaJobId    = null;
        var mediaPoll     = null;
        var mergeJobId    = null;
        var mergePoll     = null;

        // ============================================================= TAB SWITCHING
        view.querySelectorAll('.sc-main-tab-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var target = btn.getAttribute('data-sc-main');

                view.querySelectorAll('.sc-main-tab-btn').forEach(function (b) {
                    b.classList.toggle('active', b === btn);
                });
                view.querySelector('#scMainIntro').style.display    = target === 'intro'    ? '' : 'none';
                view.querySelector('#scMainMedia').style.display    = target === 'media'    ? '' : 'none';
                view.querySelector('#scMainMerge').style.display    = target === 'merge'    ? '' : 'none';
                view.querySelector('#scMainSettings').style.display = target === 'settings' ? '' : 'none';

                if (target === 'media')    loadMediaStats();
                if (target === 'merge')    loadMergeSettings();
                if (target === 'settings') loadSettings();
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
            view.querySelector('#btnRun').disabled = true;
            selSeason.innerHTML = '<option value="">- Loading seasons... -</option>';
            hideMarkers();

            apiGet('strmcompanion/series/' + seriesId + '/seasons')
                .then(function (seasons) {
                    selSeason.innerHTML = '<option value="">Entire series (all seasons)</option>';
                    (seasons || []).forEach(function (s) {
                        var opt = document.createElement('option');
                        opt.value = s.Id;
                        opt.textContent = s.Name;
                        selSeason.appendChild(opt);
                    });
                    selSeason.disabled = false;
                    view.querySelector('#btnRun').disabled = false;
                })
                .catch(function (err) {
                    console.error('StrmCompanion: seasons error', err);
                    selSeason.innerHTML = '<option value="">Error loading seasons</option>';
                });
        }

        // ---- markers ----
        function hideMarkers() {
            view.querySelector('#markersSection').style.display = 'none';
            currentSeasonId = null;
        }

        function loadMarkers(id, seriesView) {
            if (seriesView) {
                currentSeasonId = null;
            } else {
                currentSeasonId = id;
            }
            var section   = view.querySelector('#markersSection');
            var loading   = view.querySelector('#markersLoading');
            var table     = view.querySelector('#markersTable');
            var tbody     = view.querySelector('#markersBody');
            var colSeason = view.querySelector('#colSeason');
            var btnDelSeasonMarkers = view.querySelector('#btnDeleteSeasonMarkers');
            var btnDelSeasonAll     = view.querySelector('#btnDeleteSeasonAll');

            btnDelSeasonMarkers.style.display = seriesView ? 'none' : '';
            btnDelSeasonAll.style.display     = seriesView ? 'none' : '';

            section.style.display = '';
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
                                ? '<span class="sc-badge-ok">Set</span>'
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
                        showIntroResults(job);
                    }
                })
                .catch(function () { stopIntroPoll(); });
        }

        function showIntroProgress() {
            view.querySelector('#selectionForm').style.display       = 'none';
            view.querySelector('#introProgressSection').style.display = '';
            view.querySelector('#introResultsSection').style.display  = 'none';
        }

        function showIntroResults(job) {
            view.querySelector('#introProgressSection').style.display = 'none';
            view.querySelector('#introResultsSection').style.display  = '';
            var title   = view.querySelector('#introResultTitle');
            var summary = view.querySelector('#introResultSummary');
            var tbody   = view.querySelector('#introResultRows');

            if (job.Status === 'Completed') {
                title.textContent = 'Done!';
                title.style.color = '#00a4dc';
                summary.textContent = 'Intro markers saved to Emby.';
            } else if (job.Status === 'Failed') {
                title.textContent = 'Failed';
                title.style.color = '#cc0000';
                summary.textContent = job.ErrorMessage || 'An unknown error occurred.';
            } else {
                title.textContent = 'Cancelled';
                title.style.color = '#aaa';
                summary.textContent = '';
            }

            tbody.innerHTML = '';
            var results = job.EpisodeResults || {};
            Object.keys(results).forEach(function (epId) {
                var tr = document.createElement('tr');
                tr.innerHTML =
                    '<td>' + escapeHtml(String(epId)) + '</td>' +
                    '<td>' + escapeHtml(results[epId]) + '</td>';
                tbody.appendChild(tr);
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

        // ---- intro event wiring ----
        view.querySelector('#selectSeries').addEventListener('change', function () {
            currentSeriesId = this.value || null;
            if (!currentSeriesId) {
                hideMarkers();
                var sel = view.querySelector('#selectSeason');
                sel.innerHTML = '<option value="">- Select a series first -</option>';
                sel.disabled = true;
                view.querySelector('#btnRun').disabled = true;
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
            confirmDelete('Delete ALL markers and fingerprint cache for this season?',
                'strmcompanion/intro/all/season/' + currentSeasonId, reloadCurrentMarkers);
        });

        view.querySelector('#btnDeleteSeriesMarkers').addEventListener('click', function () {
            if (!currentSeriesId) return;
            confirmDelete('Delete ALL intro markers for the entire series?',
                'strmcompanion/intro/markers/series/' + currentSeriesId, reloadCurrentMarkers);
        });

        view.querySelector('#btnDeleteSeriesAll').addEventListener('click', function () {
            if (!currentSeriesId) return;
            confirmDelete('Delete ALL markers and fingerprint cache for the entire series?',
                'strmcompanion/intro/all/series/' + currentSeriesId, reloadCurrentMarkers);
        });

        view.querySelector('#btnRun').addEventListener('click', function () {
            var seriesId = parseInt(view.querySelector('#selectSeries').value, 10);
            if (!seriesId) return;
            var seasonVal = view.querySelector('#selectSeason').value;
            var body = { SeriesId: seriesId };
            if (seasonVal) body.SeasonId = parseInt(seasonVal, 10);
            apiPost('strmcompanion/intro/run', body)
                .then(function (r) { introJobId = r.JobId; showIntroProgress(); startIntroPoll(); })
                .catch(function (err) {
                    console.error('StrmCompanion: run failed', err);
                    window.Dashboard.alert('Could not start the job.');
                });
        });

        view.querySelector('#btnCancel').addEventListener('click', function () {
            if (!introJobId) return;
            apiDelete('strmcompanion/intro/job/' + introJobId);
            stopIntroPoll();
            introJobId = null;
            showIntroResults({ Status: 'Cancelled', EpisodeResults: {} });
        });

        view.querySelector('#btnReset').addEventListener('click', function () {
            introJobId = null;
            stopIntroPoll();
            view.querySelector('#introProgressBar').style.width = '0%';
            view.querySelector('#introStatusText').textContent  = 'Preparing...';
            view.querySelector('#selectionForm').style.display       = '';
            view.querySelector('#introProgressSection').style.display = 'none';
            view.querySelector('#introResultsSection').style.display  = 'none';
            reloadCurrentMarkers();
        });

        // ============================================================= MEDIA INFO

        function loadMediaStats() {
            var loading = view.querySelector('#statsLoading');
            var bar     = view.querySelector('#statsBar');
            var errEl   = view.querySelector('#statsError');
            var warnEl  = view.querySelector('#noLibrariesWarn');

            // Stats bar is always visible (shows – placeholders); spinner shows during fetch
            loading.style.display = '';
            errEl.style.display   = 'none';

            // Fetch settings and stats in parallel
            Promise.all([
                apiGet('strmcompanion/mediainfo/settings'),
                apiGet('strmcompanion/mediainfo/stats')
            ]).then(function (results) {
                var cfg   = results[0];
                var stats = results[1];

                loading.style.display = 'none';

                var hasLibs = cfg.MediaInfoLibraryIds && cfg.MediaInfoLibraryIds.length > 0;
                warnEl.style.display = hasLibs ? 'none' : '';

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
            }).catch(function (err) {
                console.error('StrmCompanion MediaInfo: stats error', err);
                loading.style.display = 'none';
                errEl.style.display   = '';
            });
        }

        function startMediaScan() {
            view.querySelector('#mediaScanControls').style.display     = 'none';
            view.querySelector('#mediaProgressSection').style.display  = '';
            view.querySelector('#mediaResultsSection').style.display   = 'none';
            view.querySelector('#mediaStatusText').textContent         = 'Starting...';
            view.querySelector('#mediaProgressBar').style.width        = '0%';

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
        function pollMediaJob() {
            if (!mediaJobId) return;
            apiGet('strmcompanion/mediainfo/job/' + mediaJobId)
                .then(function (job) {
                    view.querySelector('#mediaProgressBar').style.width = (job.ProgressPercent || 0) + '%';
                    view.querySelector('#mediaStatusText').textContent   = job.CurrentActivity || '';
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
            var tbody     = view.querySelector('#mediaResultRows');

            if (job.Status === 'Completed') {
                titleEl.textContent   = 'Scan complete';
                summaryEl.textContent = Object.keys(job.EpisodeResults || {}).length + ' item(s) processed.';
            } else if (job.Status === 'Cancelled') {
                titleEl.textContent   = 'Scan cancelled';
                summaryEl.textContent = 'The scan was cancelled.';
            } else {
                titleEl.textContent   = 'Scan failed';
                summaryEl.textContent = job.ErrorMessage || 'An error occurred.';
            }

            tbody.innerHTML = '';
            var results = job.EpisodeResults || {};
            Object.keys(results).forEach(function (id) {
                var msg = results[id] || '';
                var isError = msg.toLowerCase().indexOf('error') === 0 || msg.toLowerCase().indexOf('no streams') === 0;
                var tr = document.createElement('tr');
                tr.innerHTML =
                    '<td>' + escapeHtml(id) + '</td>' +
                    '<td><span class="' + (isError ? 'sc-badge-err' : 'sc-badge-ok') + '">' + escapeHtml(msg) + '</span></td>';
                tbody.appendChild(tr);
            });

            loadMediaStats();
        }

        function showMediaControls() {
            view.querySelector('#mediaScanControls').style.display    = '';
            view.querySelector('#mediaProgressSection').style.display = 'none';
            view.querySelector('#mediaResultsSection').style.display  = 'none';
            mediaJobId = null;
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

        function loadSettings() {
            // Intro Detection settings
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
                .catch(function (err) { console.error('StrmCompanion: settings load error', err); });

            // Media Info settings
            apiGet('strmcompanion/mediainfo/settings')
                .then(function (cfg) {
                    view.querySelector('#numConcurrency').value = cfg.MediaInfoConcurrency || 2;
                    view.querySelector('#chkAutoScan').checked  = !!cfg.MediaInfoAutoScan;
                    var selectedIds = (cfg.MediaInfoLibraryIds || '').split(',')
                        .map(function (s) { return s.trim(); }).filter(Boolean);
                    renderLibraries(selectedIds);
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

        function saveSettings() {
            // Save intro detection settings
            var introBody = {
                FingerprintDataPath:        view.querySelector('#txtFingerprintPath').value.trim(),
                FfmpegPathOverride:          view.querySelector('#txtFfmpegPath').value.trim(),
                FingerprintDurationMinutes:  parseInt(view.querySelector('#numFingerprintMinutes').value, 10),
                MinimumIntroLengthSeconds:   parseInt(view.querySelector('#numMinIntroLength').value, 10),
                HammingDistanceThreshold:       parseInt(view.querySelector('#numHamming').value, 10),
                SilenceThresholdDb:             view.querySelector('#txtSilenceDb').value.trim(),
                SilenceDurationSeconds:         0.5,
                OverwriteExistingIntroMarkers:  view.querySelector('#chkOverwriteMarkers').checked
            };

            // Save media info settings
            var checkboxes = view.querySelectorAll('#libraryList input[type=checkbox]:checked');
            var selectedIds = Array.prototype.slice.call(checkboxes).map(function (cb) { return cb.value; }).join(',');
            var mediaBody = {
                MediaInfoLibraryIds: selectedIds,
                MediaInfoConcurrency: parseInt(view.querySelector('#numConcurrency').value, 10) || 2,
                MediaInfoAutoScan:    view.querySelector('#chkAutoScan').checked
            };

            Promise.all([
                apiPost('strmcompanion/settings', introBody),
                apiPost('strmcompanion/mediainfo/settings', mediaBody)
            ])
            .then(function (results) {
                var cfg = results[0];
                var eff = view.querySelector('#lblEffectivePath');
                if (eff && cfg) eff.textContent = 'Current path: ' + (cfg.EffectiveFingerprintPath || '(unknown)');
                var msg = view.querySelector('#settingsSavedMsg');
                msg.style.display = '';
                setTimeout(function () { msg.style.display = 'none'; }, 2500);
            })
            .catch(function (err) {
                console.error('StrmCompanion: save settings error', err);
                window.Dashboard.alert('Could not save settings.');
            });
        }

        view.querySelector('#btnSaveSettings').addEventListener('click', saveSettings);

        // ============================================================= MERGE VERSION

        function loadMergeSettings() {
            apiGet('strmcompanion/mergeversion/settings')
                .then(function (cfg) {
                    var savedIds = (cfg.MergeVersionLibraryIds || '').split(',')
                        .map(function (s) { return s.trim(); }).filter(Boolean);
                    var isGlobal = savedIds.length === 0;
                    view.querySelector('#chkMergeGlobal').checked = isGlobal;
                    renderMergeLibraries(savedIds, isGlobal);
                })
                .catch(function (err) { console.error('StrmCompanion MergeVersion: settings load error', err); });
        }

        function renderMergeLibraries(selectedIds, disableAll) {
            var container = view.querySelector('#mergeLibraryList');
            apiGet('strmcompanion/mergeversion/libraries')
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
                        cb.type     = 'checkbox';
                        cb.value    = String(lib.Id);
                        cb.checked  = !disableAll && selectedIds.indexOf(String(lib.Id)) !== -1;
                        cb.disabled = disableAll;
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

        function saveMergeSettings() {
            var isGlobal = view.querySelector('#chkMergeGlobal').checked;
            var selectedIds = '';
            if (!isGlobal) {
                var checkboxes = view.querySelectorAll('#mergeLibraryList input[type=checkbox]:checked');
                selectedIds = Array.prototype.slice.call(checkboxes)
                    .map(function (cb) { return cb.value; }).join(',');
            }
            apiPost('strmcompanion/mergeversion/settings', { MergeVersionLibraryIds: selectedIds })
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

        function pollMergeJob() {
            if (!mergeJobId) return;
            apiGet('strmcompanion/mergeversion/job/' + mergeJobId)
                .then(function (job) {
                    view.querySelector('#mergeProgressBar').style.width = (job.ProgressPercent || 0) + '%';
                    view.querySelector('#mergeStatusText').textContent   = job.CurrentActivity || '';
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

            if (job.Status === 'Completed') {
                titleEl.textContent   = 'Merge complete';
                titleEl.style.color   = '#00a4dc';
                summaryEl.textContent = Object.keys(job.EpisodeResults || {}).length + ' group(s) processed.';
            } else if (job.Status === 'Cancelled') {
                titleEl.textContent   = 'Merge cancelled';
                titleEl.style.color   = '#aaa';
                summaryEl.textContent = '';
            } else {
                titleEl.textContent   = 'Merge failed';
                titleEl.style.color   = '#cc0000';
                summaryEl.textContent = job.ErrorMessage || 'An error occurred.';
            }

            tbody.innerHTML = '';
            var results = job.EpisodeResults || {};
            Object.keys(results).forEach(function (id) {
                var msg     = results[id] || '';
                var isError = msg.toLowerCase().indexOf('error') === 0;
                var tr = document.createElement('tr');
                tr.innerHTML =
                    '<td>' + escapeHtml(id) + '</td>' +
                    '<td><span class="' + (isError ? 'sc-badge-err' : 'sc-badge-ok') + '">' + escapeHtml(msg) + '</span></td>';
                tbody.appendChild(tr);
            });
        }

        function showMergeControls() {
            view.querySelector('#mergeControls').style.display        = '';
            view.querySelector('#mergeProgressSection').style.display = 'none';
            view.querySelector('#mergeResultsSection').style.display  = 'none';
            mergeJobId = null;
        }

        view.querySelector('#chkMergeGlobal').addEventListener('change', function () {
            var isGlobal = this.checked;
            view.querySelectorAll('#mergeLibraryList input[type=checkbox]').forEach(function (cb) {
                cb.disabled = isGlobal;
                if (isGlobal) cb.checked = false;
            });
        });

        view.querySelector('#btnSaveMergeSettings').addEventListener('click', saveMergeSettings);
        view.querySelector('#btnRunMerge').addEventListener('click', startMerge);

        view.querySelector('#btnMergeCancel').addEventListener('click', function () {
            if (mergeJobId) apiDelete('strmcompanion/mergeversion/job/' + mergeJobId);
            stopMergePoll();
            showMergeControls();
        });

        view.querySelector('#btnMergeReset').addEventListener('click', showMergeControls);

        // ============================================================= FOOTER
        function initFooter() {
            // Offset footer to the right of Emby's sidebar when present
            var drawer = document.querySelector('.mainDrawer');
            var drawerRight = drawer ? drawer.getBoundingClientRect().right : 0;
            var footerLeft = (drawerRight > 0 && drawerRight < window.innerWidth * 0.5) ? drawerRight : 0;
            document.documentElement.style.setProperty('--sc-footer-left', footerLeft + 'px');

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
            view.querySelector('#selectionForm').style.display        = '';
            view.querySelector('#introProgressSection').style.display = 'none';
            view.querySelector('#introResultsSection').style.display  = 'none';
            view.querySelector('#selectSeason').disabled = true;
            view.querySelector('#btnRun').disabled        = true;
            hideMarkers();
            loadSeries();

            // Always start on Intro Detection tab
            view.querySelectorAll('.sc-main-tab-btn').forEach(function (b) {
                b.classList.toggle('active', b.getAttribute('data-sc-main') === 'intro');
            });
            view.querySelector('#scMainIntro').style.display    = '';
            view.querySelector('#scMainMedia').style.display    = 'none';
            view.querySelector('#scMainMerge').style.display    = 'none';
            view.querySelector('#scMainSettings').style.display = 'none';
        });

        view.addEventListener('viewhide', function () {
            stopIntroPoll();
            stopMediaPoll();
            stopMergePoll();
        });
    };
});
