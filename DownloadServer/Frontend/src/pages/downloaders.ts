import { api, type Downloader, type DownloadersChanged } from '../api/client';
import { auth } from '../auth';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { copyText, errorMessage, field, formatSince, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The accounts files are downloaded under: accounts of the node in the
 * downloader role, one a charging station as a rule - made here with a
 * password made up for them and shown once, given a new one, and taken away.
 *
 * A downloader taken away keeps its account and the record of what it
 * downloaded; it may only download no more. Its SSH keys, for SFTP without
 * a password, are given to it as to any account - with 'sshKeys' at the
 * command line.
 */
export const downloadersPage: Page = {

    title: 'Downloaders',

    render({ root }) {

        const content = shell(root, {
            active:    '/downloadServer/downloaders',
            title:     'Downloaders',
            subtitle:  'the accounts charging stations download under.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('downloaders', 'edit');

        let cancelled = false;
        let current: Downloader[] | null = null;

        /** The password just made, shown until the page is drawn anew from the node. */
        let shown: { downloader: string; password: string } | null = null;


        function draw(): void {

            if (current === null)
                return;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the downloaders', 'make or change them')}
                    </div>
                `}

                <div class="cards stacked">
                    ${shown !== null ? passwordCard(shown.downloader, shown.password) : nothing}
                    ${downloadersCard(current)}
                    ${addCard()}
                </div>

            `);

        }


        /** The password just made: shown this once, and never again. */
        function passwordCard(Downloader: string, Password: string): TemplateResult {

            return html`
                <section class="card" id="password-card">

                    <h2><i class="fa-solid fa-key"></i> The password of ${Downloader}</h2>

                    <p class="hint">
                        Shown this once: the download server keeps only what checks it. Give it to the charging station now -
                        as the credentials of the download URL, or of SFTP.
                    </p>

                    <div class="password-shown">
                        <code id="password">${Password}</code>
                        <button type="button" class="btn small" id="copy-password"
                                @click=${(event: Event) => void copied(event, Password)}>Copy</button>
                        <button type="button" class="btn small" id="hide-password"
                                @click=${() => { shown = null; draw(); }}>Done</button>
                    </div>

                </section>
            `;

        }


        async function copied(Event: Event, Password: string): Promise<void> {
            const button = Event.currentTarget as HTMLButtonElement;
            button.textContent = await copyText(Password);
        }


        function downloadersCard(Downloaders: Downloader[]): TemplateResult {

            return html`
                <section class="card" id="downloaders">

                    <h2><i class="fa-solid fa-charging-station"></i> Downloaders <span class="chip">${Downloaders.length}</span></h2>

                    ${Downloaders.length === 0
                          ? html`<p class="hint" id="no-downloaders">None yet: nobody may download. Make one below - one a charging station or a local controller, named as it is.</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records downloaders">
                                      <thead>
                                          <tr><th>Name</th><th>Called</th><th class="right">Downloads</th><th>Last download</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(Downloaders, downloader => downloader.id, downloader => downloaderRow(downloader))}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <div class="form-actions">
                        <span id="downloaders-note"  class="form-notice" role="status"></span>
                        <span id="downloaders-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        function downloaderRow(Downloader: Downloader): TemplateResult {

            return html`
                <tr data-downloader="${Downloader.id}">
                    <td><code>${Downloader.id}</code> ${Downloader.disabled ? html`<span class="chip bad">disabled</span>` : nothing}</td>
                    <td>${Downloader.name !== Downloader.id ? Downloader.name : ''}</td>
                    <td class="right">
                        ${Downloader.downloads > 0
                              ? html`<a href="downloadServer/downloads?downloader=${encodeURIComponent(Downloader.id)}">${Downloader.downloads}</a>`
                              : '0'}
                    </td>
                    <td>${Downloader.lastDownload !== null
                              ? html`${formatTimestamp(Downloader.lastDownload)} <span class="muted">${formatSince(Downloader.lastDownload)}</span>`
                              : html`<span class="muted">never</span>`}</td>
                    <td class="record-actions">
                        <button type="button" class="btn small" data-password="${Downloader.id}" ?disabled=${!mayChange}
                                @click=${() => void newPassword(Downloader)}>New password</button>
                        <button type="button" class="btn small danger" data-remove="${Downloader.id}" ?disabled=${!mayChange}
                                @click=${() => void remove(Downloader)}>Remove</button>
                    </td>
                </tr>
            `;

        }


        /** The form of a new downloader: the name it signs in with, and what it is called. */
        function addCard(): TemplateResult {

            return html`
                <section class="card" id="add-downloader-card">

                    <h2><i class="fa-solid fa-plus"></i> A new downloader</h2>

                    <form id="add-downloader" class="form-stack" @submit=${onAdd}>

                        <label>Name
                            <input type="text" name="id" required autocomplete="off" spellcheck="false"
                                   pattern="[A-Za-z0-9._\\-]+" maxlength="64"
                                   placeholder="CS001" ?disabled=${!mayChange} />
                            <span class="hint">
                                What it signs in with - as a rule the name of the charging station or the local controller,
                                so that the downloads say which of them fetched what. Letters, digits, '.', '-' and '_'.
                            </span>
                        </label>

                        <label>Called
                            <input type="text" name="name" autocomplete="off"
                                   placeholder="Charging station in the yard" ?disabled=${!mayChange} />
                            <span class="hint">What it is, for whoever reads the list; its name where empty.</span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Make the downloader</button>
                            <span id="add-downloader-note"  class="form-notice" role="status"></span>
                            <span id="add-downloader-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onAdd(event: SubmitEvent): void {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const id    = field(form, 'id');
            const name  = field(form, 'name');

            void change(() => api.downloadServer.addDownloader(id, name), '#add-downloader', form);

        }


        function newPassword(Downloader: Downloader): Promise<void> {

            if (!confirm(`Give ${Downloader.id} a new password? The one it has opens nothing from then on - the charging station has to be told the new one.`))
                return Promise.resolve();

            return change(() => api.downloadServer.newPassword(Downloader.id), '#downloaders');

        }


        function remove(Downloader: Downloader): Promise<void> {

            if (!confirm(`Take ${Downloader.id} away? It may download no more. The record of what it downloaded is kept.`))
                return Promise.resolve();

            return change(() => api.downloadServer.removeDownloader(Downloader.id), '#downloaders');

        }


        /**
         * Send a change, and draw the downloaders the node has then: what it
         * made of it, not what was sent - with the password it made, once.
         */
        async function change(Send:  () => Promise<DownloadersChanged>,
                              From:  '#add-downloader' | '#downloaders',
                              Form:  HTMLFormElement | null = null): Promise<void> {

            const note   = content.querySelector<HTMLElement>(`${From}-note`);
            const error  = content.querySelector<HTMLElement>(`${From}-error`);

            for (const line of content.querySelectorAll<HTMLElement>('.form-notice, .form-error'))
                line.textContent = '';

            try
            {

                const changed = await whileSaving(content, note, Send);

                current  = changed.downloaders;
                shown    = changed.password !== null && changed.downloader !== null
                               ? { downloader: changed.downloader, password: changed.password }
                               : null;

                draw();
                Form?.reset();

                const said = content.querySelector<HTMLElement>(`${From}-note`);

                if (said !== null)
                    said.textContent = changed.said;

            }
            catch (problem)
            {
                if (error !== null)
                    error.textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {

                const loaded = await api.downloadServer.downloaders();

                if (cancelled)
                    return;

                current  = loaded;
                shown    = null;

                // Loaded anew - Reload - is what the node has, the form too.
                draw();
                content.querySelector<HTMLFormElement>('#add-downloader')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The downloaders could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
