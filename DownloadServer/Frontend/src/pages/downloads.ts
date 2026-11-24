import { api, type Download, type DownloadFilter, type Downloads } from '../api/client';
import { sizeOf } from '../format';
import { logs } from '@node/logs/store';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, formatTimestamp } from '@node/ui';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * Every download from this server - by HTTP or over SFTP - with who fetched
 * which file, how much of it, from which byte on, and from where: filtered by
 * downloader and by file, the newest or the oldest first.
 *
 * The filters are no form: each choice asks the node at once, and is kept in
 * the address of the page, so that the downloads of one charging station can
 * be sent to somebody as a link. A new download is drawn as it happens, where
 * the node's event stream runs.
 */
export const downloadsPage: Page = {

    title: 'Downloads',

    render({ root, url }) {

        const content = shell(root, {
            active:    '/downloadServer/downloads',
            title:     'Downloads',
            subtitle:  'what the charging stations fetched from here.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const pageSize    = 200;

        let cancelled     = false;
        let current: Downloads | null = null;
        let take          = pageSize;
        let opened        = new Set<number>();

        const filter: DownloadFilter = {
            downloader:  url.searchParams.get('downloader') ?? undefined,
            area:        url.searchParams.get('area')       ?? undefined,
            file:        url.searchParams.get('file')       ?? undefined,
            order:       url.searchParams.get('order') === 'oldest' ? 'oldest' : 'newest'
        };


        function draw(): void {

            if (current === null)
                return;

            const downloads = current;

            render(content, html`

                <section class="card" id="downloads">

                    <h2><i class="fa-solid fa-list-check"></i> Downloads
                        <span class="chip" id="downloads-total">${downloads.total}</span>
                    </h2>

                    ${filtersView(downloads)}

                    ${downloads.downloads.length === 0
                          ? html`<p class="hint" id="no-downloads">${filter.downloader || filter.file
                                    ? 'Nothing downloaded matches the filter.'
                                    : 'Nothing was downloaded yet. The Download server page says where charging stations fetch files from.'}</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records downloads">
                                      <thead>
                                          <tr>
                                              <th>
                                                  <button type="button" class="link sort" id="sort-time" title="Sort by time"
                                                          @click=${() => setFilter({ order: filter.order === 'oldest' ? 'newest' : 'oldest' })}>
                                                      Time <i class="fa-solid ${filter.order === 'oldest' ? 'fa-arrow-up' : 'fa-arrow-down'}"></i>
                                                  </button>
                                              </th>
                                              <th>File</th>
                                              <th class="right">Sent</th>
                                              <th class="download-protocol">Via</th>
                                              <th>Downloader</th>
                                              <th>From</th>
                                          </tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(downloads.downloads, download => download.id, download => downloadRows(download))}
                                      </tbody>
                                  </table>
                              </div>

                              ${downloads.downloads.length < downloads.total ? html`
                                  <div class="form-actions">
                                      <button type="button" class="btn small" id="more" @click=${() => { take += pageSize; void load(); }}>
                                          Show ${Math.min(pageSize, downloads.total - downloads.downloads.length)} more
                                      </button>
                                      <span class="muted">${downloads.downloads.length} of ${downloads.total} shown</span>
                                  </div>
                              ` : nothing}
                          `}

                </section>

            `);

        }


        /** Who downloaded, which file, in which order: each asks at once. */
        function filtersView(Downloads: Downloads): TemplateResult {

            // What is filtered by stays offered even where the node no longer
            // knows it - an address typed by hand - so that it can be undone.
            const downloaders  = withChosen(Downloads.downloaders, filter.downloader);
            const chosenFile   = filter.area && filter.file ? `/${filter.area}/${filter.file}` : undefined;
            const files        = withChosen(Downloads.files, chosenFile);

            return html`
                <div class="download-filters">

                    <label>File
                        <select id="filter-file" @change=${(event: Event) => setFile((event.target as HTMLSelectElement).value)}>
                            <option value="" ?selected=${!chosenFile}>every one</option>
                            ${files.map(path => html`<option value=${path} ?selected=${path === chosenFile}>${path}</option>`)}
                        </select>
                    </label>

                    <label>Downloader
                        <select id="filter-downloader" @change=${(event: Event) => setFilter({ downloader: (event.target as HTMLSelectElement).value || undefined })}>
                            <option value="" ?selected=${!filter.downloader}>every one</option>
                            ${downloaders.map(name => html`<option value=${name} ?selected=${name === filter.downloader}>${name}</option>`)}
                        </select>
                    </label>

                    <label>Order
                        <select id="filter-order" @change=${(event: Event) => setFilter({ order: (event.target as HTMLSelectElement).value === 'oldest' ? 'oldest' : 'newest' })}>
                            <option value="newest" ?selected=${filter.order !== 'oldest'}>newest first</option>
                            <option value="oldest" ?selected=${filter.order === 'oldest'}>oldest first</option>
                        </select>
                    </label>

                    ${filter.downloader || filter.file ? html`
                        <button type="button" class="btn small" id="filter-clear"
                                @click=${() => setFilter({ downloader: undefined, area: undefined, file: undefined })}>Show all</button>
                    ` : nothing}

                </div>
            `;

        }


        /** One download, and below it - where opened - everything else known of it. */
        function downloadRows(Download: Download): TemplateResult {

            const open = opened.has(Download.id);

            return html`
                <tr data-download="${Download.id}" class="download ${open ? 'opened' : ''}" @click=${(event: Event) => toggle(event, Download.id)}>
                    <td class="download-time" title="${Download.timestamp}">
                        ${formatTimestamp(Download.timestamp)} <span class="muted">${formatSince(Download.timestamp)}</span>
                    </td>
                    <td class="download-file">
                        <button type="button" class="link" title="Only the downloads of /${Download.area}/${Download.fileName}"
                                @click=${(event: Event) => { event.stopPropagation(); setFilter({ area: Download.area, file: Download.fileName }); }}>/${Download.area}/${Download.fileName}</button>
                    </td>
                    <td class="right">
                        ${sizeOf(Download.bytes)}
                        ${Download.firstByte > 0 ? html`<span class="muted">from byte ${Download.firstByte}</span>` : nothing}
                        ${Download.toTheEnd ? nothing : html`<span class="chip warn">not to the end</span>`}
                    </td>
                    <td class="download-protocol"><span class="chip">${Download.protocol}</span></td>
                    <td class="download-downloader">
                        <button type="button" class="link" title="Only the downloads of ${Download.downloader}"
                                @click=${(event: Event) => { event.stopPropagation(); setFilter({ downloader: Download.downloader }); }}>${Download.downloader}</button>
                    </td>
                    <td class="download-from">${Download.remoteAddress}</td>
                </tr>
                ${open ? html`
                    <tr class="download-details" data-details="${Download.id}">
                        <td colspan="6">
                            <div class="kv-list">
                                <div class="kv"><span class="k">Download</span><span class="v">${Download.id}</span></div>
                                <div class="kv"><span class="k">At</span><span class="v">${Download.timestamp}</span></div>
                                <div class="kv"><span class="k">Answered</span><span class="v">${Download.status}</span></div>
                                <div class="kv"><span class="k">Bytes</span><span class="v">${Download.bytes} of ${Download.size}, from byte ${Download.firstByte} on</span></div>
                                ${Download.userAgent ? html`<div class="kv"><span class="k">User agent</span><span class="v"><code>${Download.userAgent}</code></span></div>` : nothing}
                            </div>
                        </td>
                    </tr>
                ` : nothing}
            `;

        }


        function toggle(Event: Event, Id: number): void {

            // A click that selects text is somebody copying, not opening.
            if ((window.getSelection()?.toString() ?? '').length > 0 || (Event.target as HTMLElement).closest('a, button') !== null)
                return;

            if (opened.has(Id))
                opened.delete(Id);
            else
                opened.add(Id);

            draw();

        }


        /** A file chosen by its path - "/firmware/station-4.2.1.bin" - or none. */
        function setFile(Path: string): void {

            const parts = Path.split('/').filter(part => part.length > 0);

            setFilter(parts.length === 2
                          ? { area: parts[0], file: parts[1] }
                          : { area: undefined, file: undefined });

        }


        /** A filter changed: in the address of the page - replaced, not pushed - and asked of the node at once. */
        function setFilter(Change: Partial<DownloadFilter>): void {

            Object.assign(filter, Change);

            take    = pageSize;
            opened  = new Set();

            const query = new URLSearchParams();

            if (filter.area && filter.file) { query.set('area', filter.area); query.set('file', filter.file); }
            if (filter.downloader)          query.set('downloader', filter.downloader);
            if (filter.order === 'oldest')  query.set('order',      'oldest');

            const search = query.toString();

            history.replaceState(history.state, '', `${location.pathname}${search.length > 0 ? `?${search}` : ''}`);

            void load();

        }


        async function load(): Promise<void> {

            try
            {

                const loaded = await api.downloadServer.downloads({ ...filter, take });

                if (cancelled)
                    return;

                current = loaded;
                draw();

            }
            catch (error)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The downloads could not be loaded: ${errorMessage(error)}</div>
                    `);
            }

        }


        // A new download: asked anew - once, however many arrive at a time.
        let again: ReturnType<typeof setTimeout> | null = null;

        function soon(): void {
            if (again === null)
                again = setTimeout(() => { again = null; if (!cancelled) void load(); }, 300);
        }

        const stopDownloads = logs.onEvent<Download>('download', soon);

        void load();

        return () => {
            cancelled = true;
            stopDownloads();
            if (again !== null)
                clearTimeout(again);
        };

    }

};


/** The names offered, with the one chosen among them where it is not. */
function withChosen(Names: string[], Chosen: string | undefined): string[] {
    return Chosen && !Names.includes(Chosen) ? [ Chosen, ...Names ] : Names;
}
