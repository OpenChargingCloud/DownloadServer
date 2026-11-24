import { api, type DownloadServerStatus } from '../api/client';
import { reachableURL, sizeOf, thisHost } from '../format';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, formatTimestamp } from '@node/ui';
import { html, nothing, render, type TemplateResult } from '@node/view';

/**
 * How the download server offers its files - by HTTP GET on a port of its
 * own, and over SFTP on the SSH server - with what a charging station is told
 * to download from, and what it offers.
 *
 * Read-only: where files are offered is the "downloadServer" section's to
 * say, what is offered the Files page's, and who may download the
 * Downloaders page's.
 */
export const downloadServerPage: Page = {

    title: 'Download server',

    render({ root }) {

        const content = shell(root, {
            active:    '/downloadServer',
            title:     'Download server',
            subtitle:  'how charging stations fetch what is offered here.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled = false;


        function httpCard(Status: DownloadServerStatus): TemplateResult {

            const http = Status.http;

            if (http === null)
                return html`
                    <section class="card" id="http-downloads">
                        <h2><i class="fa-solid fa-download"></i> HTTP downloads <span class="chip">off</span></h2>
                        <p class="hint">The "downloadServer" section says "http": false: nothing is served by HTTP GET.</p>
                    </section>
                `;

            const url   = reachableURL(http.url);
            const area  = Status.areas[0]?.name ?? 'firmware';

            return html`
                <section class="card" id="http-downloads">

                    <h2><i class="fa-solid fa-download"></i> HTTP downloads
                        <span class="chip ${http.listening ? 'ok' : 'bad'}">${http.listening ? 'listening' : 'not listening'}</span>
                        ${http.tls ? html`<span class="chip on">TLS</span>` : html`<span class="chip warn">plain HTTP</span>`}
                    </h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Download from</span><span class="v"><code>${url}&lt;area&gt;/&lt;file name&gt;</code></span></div>
                        <div class="kv"><span class="k">Signed in</span><span class="v">${http.anonymous
                            ? html`with the credentials of a downloader - or <strong>without any</strong>: "anonymousHTTP" is true`
                            : 'with the credentials of a downloader, by HTTP Basic Auth'}</span></div>
                        <div class="kv"><span class="k">Ranges</span><span class="v">one range of bytes a request: a download that stopped is taken up where it stopped</span></div>
                        <div class="kv"><span class="k">Refused</span><span class="v">${http.refused} since the start</span></div>
                    </div>

                    <p class="hint">
                        What a charging station is given as the location of OCPP's UpdateFirmware - with the credentials
                        of its account in it:
                    </p>
                    <pre class="ask-with">${url.replace('://', '://CS001:<password>@')}${area}/station-4.2.1.bin</pre>

                    <p class="hint">Or, to try it by hand:</p>
                    <pre class="ask-with">curl -u CS001:&lt;password&gt; -O ${url}${area}/station-4.2.1.bin</pre>

                    ${http.tls ? nothing : html`
                        <p class="hint warn-line">
                            Over plain HTTP the file can be changed on its way, and the password crosses the network in
                            the clear. Give the web interface a certificate ("web.tls"), which the download port shows as
                            well, or put a proxy that speaks TLS in front.
                        </p>
                    `}

                </section>
            `;

        }


        function sftpCard(Status: DownloadServerStatus): TemplateResult {

            const sftp = Status.sftp;

            if (sftp === null)
                return html`
                    <section class="card" id="sftp-downloads">
                        <h2><i class="fa-solid fa-terminal"></i> SFTP <span class="chip">off</span></h2>
                        <p class="hint">The "downloadServer" section says "sftp": false: nothing is offered over SFTP.</p>
                    </section>
                `;

            const port  = sftp.port ?? 22;
            const area  = Status.areas[0]?.name ?? 'firmware';

            return html`
                <section class="card" id="sftp-downloads">

                    <h2><i class="fa-solid fa-terminal"></i> SFTP
                        <span class="chip ${sftp.serving ? 'ok' : 'bad'}">${sftp.serving ? 'served' : 'not served'}</span>
                    </h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Download from</span><span class="v"><code>sftp://${thisHost()}:${port}/&lt;area&gt;/&lt;file name&gt;</code></span></div>
                        <div class="kv"><span class="k">Signed in</span><span class="v">${sftp.passwords
                            ? 'as a downloader, with its password or one of its SSH keys'
                            : html`as a downloader, with one of its SSH keys - passwords open nothing, since the "ssh" section does not say "passwords": true`}</span></div>
                        <div class="kv"><span class="k">Published</span><span class="v">over SFTP as well, by whoever may publish files: put in its area, a file is offered once it is complete</span></div>
                    </div>

                    ${sftp.serving ? nothing : html`
                        <p class="hint warn-line">
                            The node serves no SFTP: its SSH server is off, or the "ssh" section says "sftp": false. See SSH server.
                        </p>
                    `}

                    <pre class="ask-with">sftp -P ${port} CS001@${thisHost()}
sftp&gt; get /${area}/station-4.2.1.bin</pre>

                </section>
            `;

        }


        function offeredCard(Status: DownloadServerStatus): TemplateResult {

            const latest = Status.latest;

            return html`
                <section class="card" id="offered">

                    <h2><i class="fa-solid fa-box-open"></i> Offered</h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Files</span><span class="v">${Status.files} <span class="muted">(${sizeOf(Status.totalSize)})</span></span></div>
                        <div class="kv"><span class="k">Areas</span><span class="v">${Status.areas.map((area, index) => html`${index > 0 ? ', ' : ''}<code>/${area.name}</code> <span class="muted">(${area.files})</span>`)}</span></div>
                        <div class="kv"><span class="k">Directory</span><span class="v"><code>${Status.directory}</code></span></div>
                        <div class="kv"><span class="k">Largest file</span><span class="v">${sizeOf(Status.maxFileSize)} to publish - through the web interface in parts of ${sizeOf(Status.webPartSize)}</span></div>
                        <div class="kv"><span class="k">Downloads</span><span class="v">${Status.downloads} <span class="muted">(${sizeOf(Status.downloadedBytes)} by ${Status.downloaders} downloader(s))</span></span></div>
                        <div class="kv"><span class="k">Newest</span><span class="v">${latest === null
                            ? 'nothing downloaded yet'
                            : html`<code>/${latest.area}/${latest.fileName}</code> by ${latest.downloader}, ${sizeOf(latest.bytes)} by ${latest.protocol},
                                   ${formatTimestamp(latest.timestamp)} <span class="muted">(${formatSince(latest.timestamp)})</span>`}</span></div>
                    </div>

                    <p class="hint"><a href="downloadServer/files">All files</a> - <a href="downloadServer/downloads">All downloads</a></p>

                </section>
            `;

        }


        async function load(): Promise<void> {

            try
            {

                const status = await api.downloadServer.status();

                if (cancelled)
                    return;

                render(content, html`
                    <div class="cards">
                        ${offeredCard(status)}
                        ${httpCard(status)}
                        ${sftpCard(status)}
                    </div>
                `);

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The download server could not be asked: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};
