import { api, type Files, type OfferedFile } from '../api/client';
import { auth } from '../auth';
import { sizeOf } from '../format';
import { logs } from '@node/logs/store';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * Every file the download server offers, area by area, with its size, when
 * it was written, its SHA-256 and how often it was downloaded - each to save
 * and to take away -, and a file to publish.
 *
 * The disk is what is offered: a file put in an area's directory by hand, or
 * published over SFTP, is here as well. Through this page a file is as large
 * as the web interface takes a request; a larger one is published over SFTP.
 * A file published, taken away or downloaded elsewhere is drawn as it
 * happens, where the node's event stream runs.
 */
export const filesPage: Page = {

    title: 'Files',

    render({ root, url }) {

        const content = shell(root, {
            active:    '/downloadServer/files',
            title:     'Files',
            subtitle:  'what charging stations fetch from here.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayPublish  = auth.can('files', 'edit');

        let cancelled     = false;
        let current: Files | null = null;
        let webMaxFileSize = 0;
        let area          = url.searchParams.get('area') ?? '';
        let opened        = new Set<string>();
        let said          = '';
        let problem       = '';


        function draw(): void {

            if (current === null)
                return;

            const files = current;

            render(content, html`

                ${mayPublish ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the files', 'publish or take them away')}
                    </div>
                `}

                <div class="cards stacked">

                    <section class="card" id="files">

                        <h2><i class="fa-solid fa-file-arrow-down"></i> Files
                            <span class="chip" id="files-total">${files.files.length}</span>
                        </h2>

                        <div class="file-filters">
                            <label>Area
                                <select id="filter-area" @change=${(event: Event) => setArea((event.target as HTMLSelectElement).value)}>
                                    <option value="" ?selected=${area === ''}>every one</option>
                                    ${files.areas.map(name => html`<option value=${name} ?selected=${name === area}>/${name}</option>`)}
                                </select>
                            </label>
                        </div>

                        ${files.files.length === 0
                              ? html`<p class="hint" id="no-files">${area !== ''
                                        ? `Nothing is offered in /${area}.`
                                        : 'Nothing is offered yet. Publish a file below, over SFTP, or put it in its area\'s directory.'}</p>`
                              : html`
                                  <div class="table-scroll">
                                      <table class="records files">
                                          <thead>
                                              <tr>
                                                  <th>File</th>
                                                  <th class="right">Size</th>
                                                  <th>Written</th>
                                                  <th class="right">Downloads</th>
                                                  <th></th>
                                              </tr>
                                          </thead>
                                          <tbody>
                                              ${repeat(files.files, file => file.path, file => fileRows(file))}
                                          </tbody>
                                      </table>
                                  </div>
                              `}

                        <div class="form-actions">
                            <span id="files-note"  class="form-notice" role="status">${said}</span>
                            <span id="files-error" class="form-error"  role="alert">${problem}</span>
                        </div>

                    </section>

                    ${publishCard(files)}

                </div>

            `);

        }


        /** One file, and below it - where opened - its SHA-256 and the last download. */
        function fileRows(File: OfferedFile): TemplateResult {

            const open = opened.has(File.path);

            return html`
                <tr data-file="${File.path}" class="file ${open ? 'opened' : ''}" @click=${(event: Event) => toggle(event, File.path)}>
                    <td class="file-path"><code>${File.path}</code></td>
                    <td class="right">${sizeOf(File.size)}</td>
                    <td class="file-time" title="${File.modified}">
                        ${formatTimestamp(File.modified)} <span class="muted">${formatSince(File.modified)}</span>
                    </td>
                    <td class="right">
                        ${(File.downloads ?? 0) > 0
                              ? html`<a href="downloadServer/downloads?area=${encodeURIComponent(File.area)}&file=${encodeURIComponent(File.name)}">${File.downloads}</a>`
                              : '0'}
                    </td>
                    <td class="file-actions" @click=${(event: Event) => event.stopPropagation()}>
                        <a class="btn small" href=${api.downloadServer.contentURL(File.area, File.name)} download=${File.name} title="Save it">Save</a>
                        ${mayPublish ? html`
                            <button type="button" class="btn small danger" data-remove="${File.path}"
                                    @click=${() => void remove(File)}>Remove</button>
                        ` : nothing}
                    </td>
                </tr>
                ${open ? html`
                    <tr class="file-details" data-details="${File.path}">
                        <td colspan="5">
                            <div class="kv-list">
                                <div class="kv"><span class="k">Size</span><span class="v">${File.size} bytes</span></div>
                                <div class="kv"><span class="k">SHA-256</span><span class="v"><code>${File.sha256}</code></span></div>
                                <div class="kv"><span class="k">Written</span><span class="v">${File.modified}</span></div>
                                <div class="kv"><span class="k">Last download</span><span class="v">${File.lastDownload
                                    ? html`${formatTimestamp(File.lastDownload)} <span class="muted">${formatSince(File.lastDownload)}</span>`
                                    : html`<span class="muted">never</span>`}</span></div>
                            </div>
                        </td>
                    </tr>
                ` : nothing}
            `;

        }


        /** The form of a file to publish: its area, and the file. */
        function publishCard(Files: Files): TemplateResult {

            return html`
                <section class="card" id="publish-card">

                    <h2><i class="fa-solid fa-upload"></i> Publish a file</h2>

                    <form id="publish" class="form-stack" @submit=${onPublish}>

                        <label>Area
                            <select name="area" ?disabled=${!mayPublish}>
                                ${Files.areas.map(name => html`<option value=${name} ?selected=${name === (area || Files.areas[0])}>/${name}</option>`)}
                            </select>
                        </label>

                        <label>File
                            <input type="file" name="file" required ?disabled=${!mayPublish} />
                            <span class="hint">
                                Offered under its name, in the place of a file of that name. Up to ${sizeOf(webMaxFileSize)}
                                here; a larger one is published over SFTP, or put in its area's directory.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayPublish}>Publish</button>
                            <span id="publish-note"  class="form-notice" role="status"></span>
                            <span id="publish-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onPublish(event: SubmitEvent): void {

            event.preventDefault();

            const form    = event.currentTarget as HTMLFormElement;
            const target  = (form.elements.namedItem('area') as HTMLSelectElement | null)?.value ?? '';
            const file    = (form.elements.namedItem('file') as HTMLInputElement | null)?.files?.[0];
            const note    = content.querySelector<HTMLElement>('#publish-note');
            const error   = content.querySelector<HTMLElement>('#publish-error');

            if (note  !== null) note.textContent  = '';
            if (error !== null) error.textContent = '';

            if (file === undefined || target === '')
                return;

            if (webMaxFileSize > 0 && file.size > webMaxFileSize) {
                if (error !== null)
                    error.textContent = `${file.name} has ${sizeOf(file.size)}, and the web interface takes ${sizeOf(webMaxFileSize)}. Publish it over SFTP, or put it in its area's directory.`;
                return;
            }

            void (async () => {

                try
                {

                    const published = await whileSaving(content, note, () => api.downloadServer.publish(target, file.name, file));

                    form.reset();
                    said     = published.said;
                    problem  = '';

                    await load(false);

                }
                catch (failed)
                {
                    const shown = content.querySelector<HTMLElement>('#publish-error');
                    if (shown !== null)
                        shown.textContent = errorMessage(failed);
                }

            })();

        }


        function toggle(Event: Event, Path: string): void {

            // A click that selects text is somebody copying, not opening.
            if ((window.getSelection()?.toString() ?? '').length > 0 || (Event.target as HTMLElement).closest('a, button') !== null)
                return;

            if (opened.has(Path))
                opened.delete(Path);
            else
                opened.add(Path);

            draw();

        }


        /** Another area: in the address of the page - replaced, not pushed - and asked of the node at once. */
        function setArea(Area: string): void {

            area    = Area;
            opened  = new Set();

            history.replaceState(history.state, '', `${location.pathname}${area !== '' ? `?area=${encodeURIComponent(area)}` : ''}`);

            void load();

        }


        async function remove(File: OfferedFile): Promise<void> {

            if (!confirm(`Take ${File.path} away? Charging stations pointed to it fetch it no more. What was downloaded of it stays recorded.`))
                return;

            said     = '';
            problem  = '';

            try
            {
                said = (await api.downloadServer.removeFile(File.area, File.name)).said;
                await load(false);
            }
            catch (error)
            {
                problem = errorMessage(error);
                draw();
            }

        }


        async function load(ClearSaid = true): Promise<void> {

            if (ClearSaid) {
                said     = '';
                problem  = '';
            }

            try
            {

                const [loaded, status] = await Promise.all([
                    api.downloadServer.files(area !== '' ? area : undefined),
                    api.downloadServer.status()
                ]);

                if (cancelled)
                    return;

                current         = loaded;
                webMaxFileSize  = status.webMaxFileSize;

                draw();

            }
            catch (error)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The files could not be loaded: ${errorMessage(error)}</div>
                    `);
            }

        }


        // A file published, taken away or downloaded elsewhere: asked anew -
        // once, however many arrive at a time.
        let again: ReturnType<typeof setTimeout> | null = null;

        function soon(): void {
            if (again === null)
                again = setTimeout(() => { again = null; if (!cancelled) void load(false); }, 300);
        }

        const stopPublished  = logs.onEvent<OfferedFile>('filePublished', soon);
        const stopRemoved    = logs.onEvent<{ area: string; name: string }>('fileRemoved', soon);
        const stopDownloads  = logs.onEvent<unknown>('download', soon);

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => {
            cancelled = true;
            stopPublished();
            stopRemoved();
            stopDownloads();
            release();
            if (again !== null)
                clearTimeout(again);
        };

    }

};
