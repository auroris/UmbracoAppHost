import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
import { enclosingFormResource } from '../../../AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/form-context.js';

const runtime = (await readFile(new URL('../../../AppBlocks/src/AppBlocks/wwwroot/App_Plugins/ApplicationBlocks/runtime.js', import.meta.url), 'utf8'))
    .replace('import.meta.url', '"https://example.test/App_Plugins/ApplicationBlocks/runtime.js"');

const orderFields = `<input id="amount" data-field="amount" data-kind="number" data-readonly="false" type="number" step="any">
        <span id="amount-error"></span>
        <input id="customer" data-field="customer" data-kind="text" data-readonly="true" readonly><span id="customer-error"></span>
        <textarea id="notes" data-field="notes" data-readonly="false"></textarea><span id="notes-error"></span>`;

function mount(respond, query = '?id=4&unrelated=ignored', fields = orderFields, api = '/api/forms/page/form') {
    const dom = new JSDOM(`<form data-app-form data-api="${api}" data-key-parameter="id" data-return="/orders/" novalidate>
      <div class="app-form-message"></div><div class="app-form-errors" hidden></div>
      <fieldset class="app-form-body" disabled>
        ${fields}
        <button type="submit" data-save>Save</button><button type="button" data-delete>Delete</button>
      </fieldset></form>`, { url: 'https://example.test/order-form' + query, runScripts:'outside-only' });
    const requests = [];
    dom.window.fetch = async (url, options = {}) => {
        requests.push({ url: String(url), ...options });
        return respond(requests.length, options);
    };
    dom.window.eval(runtime);
    return { dom, window:dom.window, document:dom.window.document, requests };
}
const loaded = (key = '4') => ({ record:{ key, version:'1', values:{ amount:'125.34', customer:'Server-controlled', notes:'Existing notes' }, capabilities:{ create:!key, update:!!key, delete:!!key } }, definitionVersion:'published-1', antiforgeryToken:'test-token' });

test('field pickers use their enclosing form resource across layouts, regions and independent forms', () => {
    const configuration = resource => ({contentData:[{values:[{alias:'resource', value:resource}]}]});
    const contents = [
        {key:'first-form', values:[{alias:'formInterface', value:configuration('orders')}]},
        {key:'second-form', values:[{alias:'formInterface', value:JSON.stringify(configuration('contacts'))}]},
    ];
    const entry = (contentKey, items = []) => ({contentKey, areas:[{items}]});
    const layouts = [entry('columns', [entry('first-form', [entry('first-field')]), entry('region', [entry('second-form', [entry('fieldset', [entry('second-field')])])]), entry('outside-field')])];
    assert.equal(enclosingFormResource(contents, layouts, 'first-field'), 'orders');
    assert.equal(enclosingFormResource(contents, layouts, 'second-field'), 'contacts');
    assert.equal(enclosingFormResource(contents, layouts, 'outside-field'), undefined);
    assert.equal(enclosingFormResource(contents, layouts, 'not-yet-attached'), undefined);
    // Umbraco keeps newly created fields local to the editor until Submit when live editing is off.
    assert.equal(enclosingFormResource(contents, layouts, 'new-first-field', 'first-form'), 'orders');
    assert.equal(enclosingFormResource(contents, layouts, 'new-second-field', 'fieldset'), 'contacts');
    assert.equal(enclosingFormResource(contents, layouts, 'new-outside-field', 'columns'), undefined);
    assert.equal(enclosingFormResource(contents, layouts, 'new-root-field', null), undefined);
    assert.equal(enclosingFormResource(contents, layouts, 'new-unknown-field', 'missing-parent'), undefined);
    // Once inserted, the field's actual location wins over an old containing-area context.
    assert.equal(enclosingFormResource(contents, layouts, 'second-field', 'first-form'), 'contacts');
    assert.equal(enclosingFormResource(contents, layouts, 'outside-field', 'first-form'), undefined);
    contents[1].values[0].value = configuration('changed-source');
    assert.equal(enclosingFormResource(contents, layouts, 'second-field'), 'changed-source');
    contents[1].values[0].value = '{unfinished';
    assert.equal(enclosingFormResource(contents, layouts, 'second-field'), undefined);
});
async function until(predicate) {
    for (let i=0;i<100;i++) { if (predicate()) return; await new Promise(resolve=>setTimeout(resolve,5)); }
    assert.fail('The form did not settle.');
}
function submit(fixture) { fixture.document.querySelector('form').dispatchEvent(new fixture.window.Event('submit',{bubbles:true,cancelable:true})); }

test('form reads and writes preserve the authored culture and put record keys before the query', async () => {
    const fixture = mount(async () => Response.json(loaded()), '?id=4&topic=Weather&appCulture=en', orderFields, '/api/forms/page/form?appCulture=fr');
    try {
        await until(() => !fixture.document.querySelector('fieldset').disabled);
        submit(fixture);
        await until(() => fixture.requests.length === 2 && !fixture.document.querySelector('fieldset').disabled);
        for (const request of fixture.requests) {
            const url = new URL(request.url);
            assert.equal(url.searchParams.get('appCulture'), 'fr');
            assert.equal(url.searchParams.get('topic'), 'Weather');
            assert.equal(url.searchParams.get('id'), '4');
        }
        assert.equal(new URL(fixture.requests[1].url).pathname, '/api/forms/page/form/4');
    } finally { fixture.dom.window.close(); }
});

test('form submissions preserve decimal text, omit read-only fields and retain values after a conflict', async () => {
    const fixture = mount(async count => count === 1 ? Response.json(loaded()) : Response.json({errors:{'':['Record changed.']}},{status:409}));
    try {
        await until(()=>!fixture.document.querySelector('fieldset').disabled);
        assert.equal(new URL(fixture.requests[0].url).search, '?id=4&unrelated=ignored');
        assert.equal(fixture.document.querySelector('#customer').disabled, true);
        fixture.document.querySelector('#amount').value='123456789.123456789';
        fixture.document.querySelector('#notes').value='';
        submit(fixture);
        await until(()=>fixture.requests.length===2 && !fixture.document.querySelector('fieldset').disabled);
        const request=fixture.requests[1], body=JSON.parse(request.body);
        assert.equal(request.method,'PUT'); assert.equal(request.headers.RequestVerificationToken,'test-token');
        assert.deepEqual(body.values,{amount:'123456789.123456789',notes:null});
        assert.equal(body.definitionVersion,'published-1'); assert.equal(body.version,'1');
        assert.equal(fixture.document.querySelector('#amount').value,'123456789.123456789');
        assert.match(fixture.document.querySelector('.app-form-errors').textContent,/Record changed/);
        submit(fixture); await until(()=>fixture.requests.length===3 && !fixture.document.querySelector('fieldset').disabled);
        assert.equal(JSON.parse(fixture.requests[2].body).submissionKey,body.submissionKey);
    } finally { fixture.dom.window.close(); }
});

test('successful create switches the form to update mode and records the new key in its URL', async () => {
    const fixture=mount(async count=>count===1 ? Response.json(loaded(null)) : Response.json({record:loaded('501').record}), '');
    try {
        await until(()=>!fixture.document.querySelector('fieldset').disabled);
        submit(fixture); await until(()=>fixture.window.location.search==='?id=501');
        assert.equal(fixture.requests[1].method,'POST');
        assert.equal(fixture.document.querySelector('[data-delete]').hidden,false);
        submit(fixture); await until(()=>fixture.requests.length===3 && !fixture.document.querySelector('fieldset').disabled);
        assert.equal(fixture.requests[2].method,'PUT');
        assert.equal(new URL(fixture.requests[2].url).pathname, '/api/forms/page/form/501');
        assert.equal(new URL(fixture.requests[2].url).searchParams.get('id'), '501');
        assert.notEqual(JSON.parse(fixture.requests[1].body).submissionKey,JSON.parse(fixture.requests[2].body).submissionKey);
    } finally { fixture.dom.window.close(); }
});

test('Print becomes available after creation and uses the saved key with page parameters', async () => {
    const fixture = mount(async count => count === 1 ? Response.json(loaded(null)) : Response.json({record:loaded('501').record}),
        '?culture=fr-CA', orderFields + '<a data-print data-print-url="/api/paper-reports/report" data-print-key-parameter="id" hidden>Print</a>');
    try {
        await until(() => !fixture.document.querySelector('fieldset').disabled);
        const print = fixture.document.querySelector('[data-print]');
        assert.equal(print.hidden, true);
        submit(fixture);
        await until(() => !print.hidden);
        const url = new URL(print.href);
        assert.equal(url.pathname, '/api/paper-reports/report');
        assert.equal(url.searchParams.get('id'), '501');
        assert.equal(url.searchParams.get('culture'), 'fr-CA');
    } finally { fixture.dom.window.close(); }
});

test('page parameters reach load, create and update while the new record key is retained', async () => {
    const fixture = mount(async count => count === 1 ? Response.json(loaded(null)) : Response.json({record:loaded('501').record}), '?topic=Fuel%20planning');
    try {
        await until(() => !fixture.document.querySelector('fieldset').disabled);
        submit(fixture);
        await until(() => new URL(fixture.window.location.href).searchParams.get('id') === '501');
        submit(fixture);
        await until(() => fixture.requests.length === 3 && !fixture.document.querySelector('fieldset').disabled);
        assert.deepEqual(fixture.requests.map(request => new URL(request.url).searchParams.get('topic')), ['Fuel planning', 'Fuel planning', 'Fuel planning']);
        assert.equal(new URL(fixture.requests[1].url).searchParams.has('id'), false);
        assert.equal(new URL(fixture.requests[2].url).searchParams.get('id'), '501');
    } finally { fixture.dom.window.close(); }
});

test('denied loads leave the fields disabled and cannot submit', async () => {
    const fixture=mount(async()=>new Response(null,{status:403}));
    try {
        await until(()=>!fixture.document.querySelector('.app-form-errors').hidden);
        submit(fixture); await new Promise(resolve=>setTimeout(resolve,10));
        assert.equal(fixture.requests.length,1); assert.equal(fixture.document.querySelector('fieldset').disabled,true);
    } finally { fixture.dom.window.close(); }
});

test('a retired stored choice stays visible and is preserved when another field changes', async () => {
    const fields = `<select id="region" data-field="region" data-kind="text" data-readonly="false"><option value="">Select</option><option value="NORTH">North</option></select>
        <textarea id="notes" data-field="notes" data-readonly="false"></textarea>`;
    const initial = { ...loaded(), record: { ...loaded().record, values: { region:'WEST', notes:'Before' } } };
    const fixture = mount(async (count, options) => count === 1 ? Response.json(initial)
        : Response.json({ record: { ...initial.record, values:JSON.parse(options.body).values } }), '?id=4', fields);
    try {
        await until(() => !fixture.document.querySelector('fieldset').disabled);
        const select = fixture.document.querySelector('#region');
        assert.equal(select.value, 'WEST');
        assert.match(select.selectedOptions[0].textContent, /WEST/);
        fixture.document.querySelector('#notes').value = 'After';
        submit(fixture);
        await until(() => fixture.requests.length === 2 && !fixture.document.querySelector('fieldset').disabled);
        assert.deepEqual(JSON.parse(fixture.requests[1].body).values, {region:'WEST', notes:'After'});
        assert.equal(select.querySelectorAll('[data-stored-value]').length, 1);
        select.value = 'NORTH';
        submit(fixture);
        await until(() => fixture.requests.length === 3 && !fixture.document.querySelector('fieldset').disabled);
        assert.equal(JSON.parse(fixture.requests[2].body).values.region, 'NORTH');
        assert.equal(select.querySelectorAll('[data-stored-value]').length, 0);
    } finally { fixture.dom.window.close(); }
});

const defaultedFields = `<input id="customer" data-field="customer" data-kind="text" data-readonly="false" data-default="test.user"><span id="customer-error"></span>
        <input id="region" data-field="region" data-kind="text" data-readonly="false" data-default="EAST"><span id="region-error"></span>
        <input id="date" data-field="orderDate" data-kind="date" data-readonly="false" type="date" data-default="2026-10-23"><span id="date-error"></span>
        <input id="when" data-field="when" data-kind="datetime" data-readonly="false" type="datetime-local" data-default="2026-10-23T14:05:09-06:00"><span id="when-error"></span>
        <input id="urgent" data-field="urgent" data-kind="checkbox" data-readonly="false" type="checkbox" data-default="true"><span id="urgent-error"></span>
        <textarea id="notes" data-field="notes" data-kind="text" data-readonly="false"></textarea><span id="notes-error"></span>
        <input type="hidden" id="owner" data-field="owner" data-kind="text" data-readonly="false" data-label="Owner" data-default="S-1-5-21-1-2-3-1105">
        <input type="hidden" id="flag" data-field="flag" data-kind="checkbox" data-readonly="false" data-label="Flag" data-default="1">`;
const localMinutes = value => { const date = new Date(value); return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16); };

test('a new record takes the defaults where the resource left fields empty, and a saved record ignores them', async () => {
    const initialized = { record:{ key:null, version:null, values:{ customer:'', region:'NORTH', orderDate:null, when:null, urgent:null, notes:null, owner:null, flag:null }, capabilities:{ create:true } }, definitionVersion:'published-1', antiforgeryToken:'test-token' };
    const saved = { record:{ key:'501', version:'1', values:{ customer:'', region:'NORTH', orderDate:'2026-10-23', when:null, urgent:false, notes:null, owner:'S-1-5-21-1-2-3-1105', flag:true }, capabilities:{ update:true } } };
    const fixture = mount(async count => Response.json(count === 1 ? initialized : saved), '', defaultedFields);
    try {
        await until(()=>!fixture.document.querySelector('fieldset').disabled);
        const value = id => fixture.document.getElementById(id).value;
        assert.equal(value('customer'), 'test.user');
        assert.equal(value('region'), 'NORTH', "the resource's own starting value wins");
        assert.equal(value('date'), '2026-10-23');
        assert.equal(value('when'), localMinutes('2026-10-23T14:05:09-06:00'));
        assert.equal(fixture.document.getElementById('urgent').checked, true);
        assert.equal(value('notes'), '');
        assert.equal(value('owner'), 'S-1-5-21-1-2-3-1105');
        submit(fixture); await until(()=>fixture.requests.length===2 && fixture.window.location.search==='?id=501');
        assert.equal(fixture.requests[1].method, 'POST');
        assert.deepEqual(JSON.parse(fixture.requests[1].body).values, {
            customer:'test.user', region:'NORTH', orderDate:'2026-10-23', when:new Date(localMinutes('2026-10-23T14:05:09-06:00')).toISOString(),
            urgent:true, notes:null, owner:'S-1-5-21-1-2-3-1105', flag:true,
        });
        // The saved record is shown as stored: an empty customer stays empty.
        assert.equal(value('customer'), '');
        assert.equal(fixture.document.getElementById('urgent').checked, false);
    } finally { fixture.dom.window.close(); }
});

test('a form announces the record it loaded and the record it saved', async () => {
    const fixture = mount(async count => count === 1 ? Response.json(loaded(null)) : Response.json({record:loaded('501').record}), '');
    try {
        const events = [];
        fixture.document.querySelector('form').addEventListener('app-form:loaded', event => events.push(['loaded', event.detail.record.key, event.detail.definitionVersion]));
        fixture.document.querySelector('form').addEventListener('app-form:saved', event => events.push(['saved', event.detail.record.key]));
        await until(() => events.length === 1);
        assert.deepEqual(events[0], ['loaded', null, 'published-1']);
        submit(fixture);
        await until(() => events.length === 2);
        assert.deepEqual(events[1], ['saved', '501']);
    } finally { fixture.dom.window.close(); }
});

const attachmentsBlock = (owner = 'form', readOnly = false) => `<section class="app-attachments" data-app-attachments data-api="/api/attachments/page/block" data-owner="${owner}"
        data-key-parameter="id" data-read-only="${readOnly}" data-max-size="1000000" data-accept="">
        <div class="app-attachments-message"></div><div class="app-attachments-errors" hidden></div>
        <ul class="app-attachments-list"></ul>
        <div class="app-attachments-upload" hidden>
          <input type="file" form="" data-attachment-file><select form="" data-attachment-purpose><option value="">Choose</option><option value="Invoice">Invoice</option></select>
          <input type="text" form="" data-attachment-label><input type="text" form="" data-attachment-notes><button type="button" data-attachment-upload>Upload</button>
        </div></section>`;
const files = (items = []) => ({ canWrite:true, purposes:['Invoice'], maxFileSize:1000000, accept:null, items, antiforgeryToken:'att-token' });
const invoice = { id:7, fileName:'invoice.pdf', contentType:'application/pdf', size:2048, sha256:'0'.repeat(64), purpose:'Invoice', label:null, notes:'Paid', uploadedBy:'member:1', uploadedAt:'2026-10-27T12:00:00.000Z', updatedBy:'member:1', updatedAt:'2026-10-27T12:00:00.000Z', revisions:[{ id:3, fileName:'invoice-v1.pdf', contentType:'application/pdf', size:1024, sha256:'1'.repeat(64), uploadedBy:'member:1', uploadedAt:'2026-10-26T12:00:00.000Z' }] };

test('an attachments block inside a form waits for the record, lists its files with the key, and uploads with the list token', async () => {
    const fixture = mount(async (count, options) => {
        if (count === 1) return Response.json(loaded());
        if (count === 2) return Response.json(files([invoice]));
        if (count === 3) return Response.json({ ...invoice, id:8, fileName:'delivery.pdf' }, { status:201 });
        return Response.json(files([{ ...invoice, id:8, fileName:'delivery.pdf', revisions:[] }, invoice]));
    }, '?id=4&unrelated=ignored', orderFields + attachmentsBlock());
    try {
        const block = fixture.document.querySelector('[data-app-attachments]');
        await until(() => block.querySelectorAll('.app-attachment').length === 1);
        const listUrl = new URL(fixture.requests[1].url);
        assert.equal(listUrl.pathname, '/api/attachments/page/block');
        assert.equal(listUrl.searchParams.get('id'), '4');
        assert.equal(listUrl.searchParams.get('unrelated'), 'ignored');
        const row = block.querySelector('.app-attachment');
        assert.equal(row.querySelector('.app-attachment-name').textContent, 'invoice.pdf');
        assert.equal(new URL(row.querySelector('.app-attachment-name').href).pathname, '/api/attachments/page/block/7');
        assert.match(row.querySelector('.app-attachment-meta').textContent, /Invoice/);
        assert.match(row.querySelector('.app-attachment-revisions summary').textContent, /1 earlier revision/);
        assert.equal(block.querySelector('.app-attachments-upload').hidden, false);
        // Upload: a multipart body with the file and the chosen purpose, and the token the list handed out.
        const input = block.querySelector('[data-attachment-file]');
        const file = new fixture.window.File(['%PDF-1.7'], 'delivery.pdf', { type:'application/pdf' });
        Object.defineProperty(input, 'files', { value:[file] });
        block.querySelector('[data-attachment-purpose]').value = 'Invoice';
        block.querySelector('[data-attachment-label]').value = 'Box 2';
        block.querySelector('[data-attachment-upload]').click();
        await until(() => fixture.requests.length === 4 && block.querySelectorAll('.app-attachment').length === 2);
        const upload = fixture.requests[2];
        assert.equal(upload.method, 'POST');
        assert.equal(new URL(upload.url).searchParams.get('id'), '4');
        assert.equal(upload.headers.RequestVerificationToken, 'att-token');
        assert.equal(upload.body.get('file').name, 'delivery.pdf');
        assert.equal(upload.body.get('purpose'), 'Invoice');
        assert.equal(upload.body.get('label'), 'Box 2');
        assert.equal(block.querySelector('.app-attachment .app-attachment-name').textContent, 'delivery.pdf');
    } finally { fixture.dom.window.close(); }
});

test('an attachments block asks for nothing until its form has a saved record, then follows the save', async () => {
    const fixture = mount(async count => count === 1 ? Response.json(loaded(null)) : count === 2 ? Response.json({record:loaded('501').record}) : Response.json(files()),
        '', orderFields + attachmentsBlock());
    try {
        const block = fixture.document.querySelector('[data-app-attachments]');
        await until(() => !fixture.document.querySelector('fieldset').disabled);
        assert.equal(fixture.requests.length, 1);
        assert.match(block.querySelector('.app-attachments-message').textContent, /Save the record/);
        assert.equal(block.querySelector('.app-attachments-upload').hidden, true);
        submit(fixture);
        await until(() => fixture.requests.length === 3);
        assert.equal(new URL(fixture.requests[2].url).searchParams.get('id'), '501');
        await until(() => block.querySelector('.app-attachments-message').textContent === 'No files yet.');
        assert.equal(block.querySelector('.app-attachments-upload').hidden, false);
    } finally { fixture.dom.window.close(); }
});

test('a standalone attachments block loads at once with the page address and removes with the token', async () => {
    const dom = new JSDOM(attachmentsBlock('page'), { url:'https://example.test/files/?block=12&appCulture=fr', runScripts:'outside-only' });
    const requests = [];
    dom.window.fetch = async (url, options = {}) => { requests.push({ url:String(url), ...options }); return requests.length === 2 ? new Response(null, { status:204 }) : Response.json(files(requests.length === 1 ? [invoice] : [])); };
    dom.window.confirm = () => true;
    try {
        dom.window.eval(runtime);
        const block = dom.window.document.querySelector('[data-app-attachments]');
        await until(() => block.querySelectorAll('.app-attachment').length === 1);
        const listUrl = new URL(requests[0].url);
        assert.equal(listUrl.searchParams.get('block'), '12');
        assert.equal(listUrl.searchParams.has('id'), false);
        [...block.querySelectorAll('.app-attachment-actions button')].find(button => button.textContent === 'Remove').click();
        await until(() => requests.length === 3);
        assert.equal(requests[1].method, 'DELETE');
        assert.equal(new URL(requests[1].url).pathname, '/api/attachments/page/block/7');
        assert.equal(requests[1].headers.RequestVerificationToken, 'att-token');
        assert.equal(requests[1].body, undefined);
    } finally { dom.window.close(); }
});

test('hidden fields are submitted like any other, a hidden checkbox as true or false, and their errors name them', async () => {
    const fields = `<input id="amount" data-field="amount" data-kind="number" data-readonly="false" type="number" step="any"><span id="amount-error"></span>
        <input type="hidden" id="owner" data-field="owner" data-kind="text" data-readonly="false" data-label="Owner">
        <input type="hidden" id="flag" data-field="flag" data-kind="checkbox" data-readonly="false" data-label="Flag">
        <input type="hidden" id="key" data-field="key" data-kind="number" data-readonly="true" data-label="Key">`;
    const record = { record:{ key:'4', version:'1', values:{ amount:'1.5', owner:'S-1-5-21-9', flag:false, key:'4' }, capabilities:{ update:true } }, definitionVersion:'published-1', antiforgeryToken:'test-token' };
    const fixture = mount(async count => count === 1 ? Response.json(record) : Response.json({ errors:{ owner:['A value is required.'] } }, { status:422 }), '?id=4', fields);
    try {
        await until(()=>!fixture.document.querySelector('fieldset').disabled);
        assert.equal(fixture.document.getElementById('flag').value, 'false');
        submit(fixture); await until(()=>fixture.requests.length===2 && !fixture.document.querySelector('fieldset').disabled);
        // The read-only key is never written; the hidden owner and flag are.
        assert.deepEqual(JSON.parse(fixture.requests[1].body).values, { amount:'1.5', owner:'S-1-5-21-9', flag:false });
        const summary = fixture.document.querySelector('.app-form-errors');
        assert.equal(summary.hidden, false);
        assert.equal(summary.querySelector('li').textContent, 'Owner: A value is required.');
        assert.equal(summary.querySelector('a'), null, 'a hidden field cannot take focus, so its message is not a link');
    } finally { fixture.dom.window.close(); }
});

// A schedule as the server renders it: two header rows, one row with two bars in two lanes, today in the third column.
const scheduleBlock = () => `<section class="app-schedule ah-schedule" data-app-schedule data-scale="days">
      <div class="app-schedule-scroll"><table class="app-schedule-table app-schedule-table--days" data-today-column="2">
        <thead><tr><th scope="col" rowspan="2" class="app-schedule-corner"><span class="app-schedule-label"></span></th><th scope="colgroup" colspan="4" class="app-schedule-period">October 2026</th></tr>
        <tr><th scope="col" class="app-schedule-col">27</th><th scope="col" class="app-schedule-col">28</th><th scope="col" class="app-schedule-col app-schedule-col--today" aria-current="date">29</th><th scope="col" class="app-schedule-col">30</th></tr></thead>
        <tbody class="app-schedule-group"><tr class="app-schedule-row" style="--ah-lanes: 2"><th scope="row" class="app-schedule-row-label"><span class="app-schedule-label">Acme Corp</span></th>
          <td class="app-schedule-cell"><a class="app-schedule-bar app-schedule-kind-1" href="/orders-form/?id=3" style="--ah-span: 2; --ah-lane: 0" data-key="3" data-kind="NEW">Order 3<span class="app-schedule-bar__details">: Acme Corp, 27 Oct 2026 to 28 Oct 2026, NEW</span></a><a class="app-schedule-bar app-schedule-kind-2 app-schedule-bar--open" href="/orders-form/?id=4" style="--ah-span: 4; --ah-lane: 1" data-key="4" data-kind="PENDING">Order 4<span class="app-schedule-bar__details">: Acme Corp, 27 Oct 2026 onward, PENDING</span></a></td>
          <td class="app-schedule-cell"></td><td class="app-schedule-cell app-schedule-col--today"></td><td class="app-schedule-cell"></td></tr></tbody>
      </table></div></section>`;

test('a schedule scrolls its window to today and shows a bar\'s details as a tooltip on hover and focus', () => {
    const dom = new JSDOM(scheduleBlock(), { url:'https://example.test/orders-schedule/', runScripts:'outside-only' });
    const { document } = dom.window;
    try {
        // jsdom lays nothing out, so the measurements the runtime reads are given here: today starts 400px in, the label column is 192px wide.
        const today = document.querySelector('thead .app-schedule-col--today');
        Object.defineProperty(today, 'offsetLeft', { value:400 });
        Object.defineProperty(today, 'offsetWidth', { value:36 });
        Object.defineProperty(document.querySelector('.app-schedule-corner'), 'offsetWidth', { value:192 });
        dom.window.eval(runtime);
        assert.equal(document.querySelector('.app-schedule-scroll').scrollLeft, 400 - 192 - 72);
        const tooltip = document.querySelector('.app-schedule-tooltip');
        assert.equal(tooltip.hidden, true);
        assert.equal(tooltip.getAttribute('aria-hidden'), 'true');
        const [first, open] = document.querySelectorAll('.app-schedule-bar');
        first.querySelector('.app-schedule-bar__details').dispatchEvent(new dom.window.MouseEvent('mouseover', { bubbles:true }));
        assert.equal(tooltip.hidden, false);
        assert.equal(tooltip.textContent, 'Order 3: Acme Corp, 27 Oct 2026 to 28 Oct 2026, NEW');
        // Moving within the bar keeps the tooltip; leaving it hides it.
        first.dispatchEvent(new dom.window.MouseEvent('mouseout', { bubbles:true, relatedTarget:first.querySelector('.app-schedule-bar__details') }));
        assert.equal(tooltip.hidden, false);
        first.dispatchEvent(new dom.window.MouseEvent('mouseout', { bubbles:true, relatedTarget:document.body }));
        assert.equal(tooltip.hidden, true);
        open.dispatchEvent(new dom.window.FocusEvent('focusin', { bubbles:true }));
        assert.equal(tooltip.hidden, false);
        assert.match(tooltip.textContent, /onward, PENDING$/);
        open.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key:'Escape', bubbles:true }));
        assert.equal(tooltip.hidden, true);
        open.dispatchEvent(new dom.window.FocusEvent('focusin', { bubbles:true }));
        open.dispatchEvent(new dom.window.FocusEvent('focusout', { bubbles:true }));
        assert.equal(tooltip.hidden, true);
        // Every bar stays a link the keyboard reaches, with its details inside it for screen readers; nothing was added to the table.
        assert.equal(open.getAttribute('href'), '/orders-form/?id=4');
        assert.equal(document.querySelectorAll('.app-schedule-bar__details').length, 2);
        assert.equal(document.querySelectorAll('.app-schedule-tooltip').length, 1);
    } finally { dom.window.close(); }
});

test('a schedule whose window has no today keeps its scroll position', () => {
    const dom = new JSDOM(scheduleBlock().replace(/ app-schedule-col--today/g, '').replace(' aria-current="date"', ''), { url:'https://example.test/orders-schedule/', runScripts:'outside-only' });
    try {
        dom.window.eval(runtime);
        assert.equal(dom.window.document.querySelector('.app-schedule-scroll').scrollLeft, 0);
        assert.ok(dom.window.document.querySelector('.app-schedule-tooltip'));
    } finally { dom.window.close(); }
});

const tabularBlock = (owner = 'form', readOnly = false) => `<section class="app-tabular" id="tabular-lines" data-app-tabular data-api="/api/tabular/page/lines" data-owner="${owner}"
        data-key-parameter="id" data-read-only="${readOnly}" data-max-rows="50" data-empty-message="No lines yet.">
        <div class="app-tabular-message" role="status"></div><div class="app-tabular-errors" role="alert" tabindex="-1" hidden></div>
        <div class="app-tabular-scroll"><table class="app-tabular-table"><thead><tr><th scope="col" data-column="item">Item *</th>
          <th scope="col" class="app-tabular-actions-head"><span class="visually-hidden">Actions</span></th></tr></thead><tbody aria-busy="true"></tbody></table></div>
        <div class="app-tabular-toolbar" hidden><button type="button" form="" data-tabular-add>Add row</button><button type="button" form="" data-tabular-save>Save rows</button></div>
        </section>`;
const lineField = (key, label, kind, more = {}) => ({ key, label, kind, required:false, readOnly:false, maxLength:null, minimum:null, maximum:null, choices:null, hasServerDefault:false, ...more });
const lineFields = [
    lineField('item', 'Item', 'text', { required:true, maxLength:200 }),
    lineField('quantity', 'Quantity', 'number', { required:true, minimum:1, maximum:9999, hasServerDefault:true }),
    lineField('shipOn', 'Ship on', 'date'),
    lineField('deliverAt', 'Deliver at', 'datetime'),
    lineField('gift', 'Gift', 'checkbox'),
    lineField('colour', 'Colour', 'text', { choices:[{ value:'red', label:'Red' }, { value:'blue', label:'Blue' }] }),
    lineField('createdBy', 'Created by', 'text', { readOnly:true }),
];
const line = (key, version, values) => ({ key, version, values:{ item:null, quantity:null, shipOn:null, deliverAt:null, gift:false, colour:null, createdBy:'member:1', ...values },
    capabilities:{ create:false, update:true, delete:true } });
const widget = line('12', '1', { item:'Widget', quantity:2, shipOn:'2026-11-02', deliverAt:'2026-11-03T09:30:00.000Z', gift:true, colour:'red' });
const bolt = line('13', '2', { item:'Bolt', quantity:5 });
const lines = (rows = [widget, bolt]) => ({ owner:{ alias:'orders', key:'4' }, dataSource:'orders.lines', canAdd:true, canUpdate:true, canDelete:true, maxRows:50,
    fields:lineFields, rows, newRow:{ key:null, version:null, values:{ item:null, quantity:1, shipOn:null, deliverAt:null, gift:false, colour:null, createdBy:null },
        capabilities:{ create:true, update:false, delete:false } }, antiforgeryToken:'tab-token' });
// The order form, then the lines, then whatever the save answers.
const mountLines = saved => mount(async count => count === 1 ? Response.json(loaded()) : count === 2 ? Response.json(lines()) : saved(count), '?id=4&unrelated=ignored', orderFields + tabularBlock());
const rowsOf = block => [...block.querySelectorAll('tbody tr[data-row-key]')];
const cell = (row, column) => row.querySelector(`[data-column="${column}"] input, [data-column="${column}"] select`);
const sentBody = request => JSON.parse(request.body);

test('a tabular form inside a form waits for the record, then lists its rows with one input per kind', async () => {
    const fixture = mountLines(() => assert.fail('Nothing is saved.'));
    try {
        const block = fixture.document.querySelector('[data-app-tabular]');
        await until(() => rowsOf(block).length === 2);
        assert.equal(new URL(fixture.requests[0].url).pathname, '/api/forms/page/form');
        const listUrl = new URL(fixture.requests[1].url);
        assert.equal(listUrl.pathname, '/api/tabular/page/lines');
        assert.equal(listUrl.searchParams.get('id'), '4');
        assert.equal(listUrl.searchParams.get('unrelated'), 'ignored');
        assert.equal(block.querySelector('tbody').getAttribute('aria-busy'), 'false');
        assert.equal(block.querySelector('.app-tabular-toolbar').hidden, false);
        assert.deepEqual([...block.querySelectorAll('thead th[data-column]')].map(th => th.textContent), ['Item *', 'Quantity *', 'Ship on', 'Deliver at', 'Gift', 'Colour', 'Created by']);
        const [first] = rowsOf(block);
        assert.equal(first.dataset.rowKey, '12');
        const item = cell(first, 'item');
        assert.equal(item.type, 'text'); assert.equal(item.value, 'Widget'); assert.equal(item.maxLength, 200);
        assert.equal(item.required, true); assert.equal(item.getAttribute('aria-required'), 'true'); assert.equal(item.getAttribute('aria-label'), 'Item');
        const quantity = cell(first, 'quantity');
        assert.equal(quantity.type, 'number'); assert.equal(quantity.value, '2'); assert.equal(quantity.step, 'any');
        assert.equal(quantity.min, '1'); assert.equal(quantity.max, '9999');
        assert.equal(cell(first, 'shipOn').type, 'date'); assert.equal(cell(first, 'shipOn').value, '2026-11-02');
        assert.equal(cell(first, 'deliverAt').type, 'datetime-local'); assert.equal(cell(first, 'deliverAt').value, localMinutes('2026-11-03T09:30:00.000Z'));
        assert.equal(cell(first, 'gift').type, 'checkbox'); assert.equal(cell(first, 'gift').checked, true);
        const colour = cell(first, 'colour');
        assert.equal(colour.tagName, 'SELECT'); assert.equal(colour.value, 'red'); assert.deepEqual([...colour.options].map(o => o.value), ['', 'red', 'blue']);
        // A read-only field is the value as text, not an input.
        assert.equal(first.querySelector('td[data-column="createdBy"]').textContent, 'member:1');
        assert.equal(cell(first, 'createdBy'), null);
        // The enclosing form neither collects nor submits the cells.
        for (const control of block.querySelectorAll('input, select, button')) {
            assert.equal(control.getAttribute('form'), '');
            assert.equal(control.hasAttribute('data-field'), false);
            assert.equal(control.form, null);
        }
    } finally { fixture.dom.window.close(); }
});

test('Add row starts from the new row and Save rows posts one create with every column, the token and a submission key', async () => {
    const created = line('14', '1', { item:'Bolt', quantity:1 });
    const fixture = mountLines(() => Response.json(lines([widget, bolt, created])));
    try {
        const block = fixture.document.querySelector('[data-app-tabular]');
        await until(() => rowsOf(block).length === 2);
        const announced = [];
        block.addEventListener('app-tabular:saved', event => announced.push(event.detail));
        block.querySelector('[data-tabular-add]').click();
        const added = block.querySelector('tbody tr.app-tabular-row--new');
        assert.ok(added);
        assert.equal(cell(added, 'quantity').value, '1');
        assert.equal(fixture.document.activeElement, cell(added, 'item'));
        cell(added, 'item').value = 'Bolt';
        block.querySelector('[data-tabular-save]').click();
        await until(() => block.querySelector('.app-tabular-message').textContent === 'Saved.');
        const request = fixture.requests[2];
        assert.equal(request.method, 'POST');
        assert.equal(new URL(request.url).searchParams.get('id'), '4');
        assert.equal(request.headers.RequestVerificationToken, 'tab-token');
        assert.equal(request.headers['Content-Type'], 'application/json');
        const body = sentBody(request);
        assert.match(body.submissionKey, /^[0-9a-f-]{36}$/);
        assert.deepEqual(body.changes, [{ operation:'create', values:{ item:'Bolt', quantity:'1', shipOn:null, deliverAt:null, gift:false, colour:null } }]);
        // The answer is the list afterwards.
        assert.equal(rowsOf(block).length, 3);
        assert.equal(block.querySelector('tbody tr.app-tabular-row--new'), null);
        assert.equal(announced.length, 1);
        assert.equal(announced[0].rows.length, 3);
    } finally { fixture.dom.window.close(); }
});

test('editing one cell posts one update with only that column, the key and the version', async () => {
    const fixture = mountLines(() => Response.json(lines([{ ...widget, version:'2', values:{ ...widget.values, quantity:3 } }, bolt])));
    try {
        const block = fixture.document.querySelector('[data-app-tabular]');
        await until(() => rowsOf(block).length === 2);
        cell(rowsOf(block)[0], 'quantity').value = '3';
        block.querySelector('[data-tabular-save]').click();
        await until(() => fixture.requests.length === 3);
        assert.deepEqual(sentBody(fixture.requests[2]).changes, [{ operation:'update', key:'12', version:'1', values:{ quantity:'3' } }]);
        await until(() => block.querySelector('.app-tabular-message').textContent === 'Saved.');
        assert.equal(cell(rowsOf(block)[0], 'quantity').value, '3');
    } finally { fixture.dom.window.close(); }
});

test('a refused batch marks the failing row and cell, keeps the typed values and focuses the errors', async () => {
    const fixture = mountLines(() => Response.json({ status:422, index:0, errors:{ quantity:['The number is outside the allowed range.'] } }, { status:422 }));
    try {
        const block = fixture.document.querySelector('[data-app-tabular]');
        await until(() => rowsOf(block).length === 2);
        const [first, second] = rowsOf(block);
        cell(first, 'quantity').value = '7';
        cell(second, 'item').value = 'Bolt, zinc';
        block.querySelector('[data-tabular-save]').click();
        const errors = block.querySelector('.app-tabular-errors');
        await until(() => !errors.hidden);
        assert.equal(sentBody(fixture.requests[2]).changes.length, 2);
        assert.match(errors.textContent, /Row 1, Quantity: The number is outside the allowed range\./);
        assert.equal(fixture.document.activeElement, errors);
        assert.ok(first.classList.contains('app-tabular-row--invalid'));
        assert.equal(cell(first, 'quantity').getAttribute('aria-invalid'), 'true');
        assert.equal(cell(first, 'item').hasAttribute('aria-invalid'), false);
        assert.equal(cell(first, 'quantity').value, '7');
        assert.equal(cell(second, 'item').value, 'Bolt, zinc');
        assert.equal(block.querySelector('[data-tabular-save]').disabled, false);
        assert.equal(block.querySelector('tbody').getAttribute('aria-busy'), 'false');
    } finally { fixture.dom.window.close(); }
});

test('a row marked for deletion posts a delete with its key and version, and a new row marked so is dropped', async () => {
    const fixture = mountLines(() => Response.json(lines([widget])));
    try {
        const block = fixture.document.querySelector('[data-app-tabular]');
        await until(() => rowsOf(block).length === 2);
        const bolts = rowsOf(block)[1];
        const remove = [...bolts.querySelectorAll('button')].find(button => button.textContent === 'Delete');
        remove.click();
        assert.ok(bolts.classList.contains('app-tabular-row--deleted'));
        assert.equal(remove.textContent, 'Undo');
        assert.equal(cell(bolts, 'item').getAttribute('aria-disabled'), 'true');
        block.querySelector('[data-tabular-add]').click();
        const added = block.querySelector('tbody tr.app-tabular-row--new');
        [...added.querySelectorAll('button')].find(button => button.textContent === 'Delete').click();
        block.querySelector('[data-tabular-save]').click();
        await until(() => fixture.requests.length === 3);
        assert.deepEqual(sentBody(fixture.requests[2]).changes, [{ operation:'delete', key:'13', version:'2' }]);
        await until(() => rowsOf(block).length === 1);
    } finally { fixture.dom.window.close(); }
});
