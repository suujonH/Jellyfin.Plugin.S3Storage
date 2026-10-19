define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    const pluginId = '7637e67a-7fd1-45a2-8b34-e69170cb470f';
    return function (view) {
        let config;
        let selected = -1;
        const fields = [...view.querySelectorAll('[data-field]')];
        const select = view.querySelector('#storageBucket');
        function saveBucket() {
            if (selected < 0) return;
            fields.forEach(field => { config.Buckets[selected][field.dataset.field] = field.type === 'checkbox' ? field.checked : field.value; });
        }
        function updateVisibility() {
            const enabled = view.querySelector('.storageEnabled').checked;
            view.querySelectorAll('.storageSettings input, .storageSettings select, .storageSettings button').forEach(el => { el.disabled = !enabled; });
            const mode = view.querySelector('#storageDelivery').value;
            view.querySelector('.publicAddress').hidden = mode !== 'Worker' && mode !== 'Public';
            view.querySelector('.workerSecret').hidden = mode !== 'Worker';
            view.querySelector('.workerManagement').hidden = mode !== 'Worker';
            view.querySelector('.mountSettings').hidden = !view.querySelector('.mountWithS3fs').checked;
            view.querySelector('#storagePublicUrl').required = enabled && (mode === 'Worker' || mode === 'Public');
            view.querySelector('#storageSigningSecret').required = enabled && mode === 'Worker';
            view.querySelector('#workerAllowedOrigins').required = enabled && mode === 'Worker';
            if (selected < 0) fields.forEach(field => { field.disabled = true; });
        }
        function showBucket(index) {
            selected = index;
            select.replaceChildren();
            config.Buckets.forEach((bucket, i) => select.add(new Option(bucket.Name, String(i))));
            select.value = String(index);
            view.querySelector('.bucketFields').hidden = index < 0;
            fields.forEach(field => { const value = index < 0 ? '' : config.Buckets[index][field.dataset.field]; if (field.type === 'checkbox') field.checked = !!value; else field.value = value || ''; field.disabled = index < 0; });
            view.querySelector('.testResult').textContent = '';
            updateVisibility();
            if (index < 0) fields.forEach(field => { field.disabled = true; });
        }
        view.addEventListener('viewshow', async function () {
            loading.show();
            try {
                config = await ApiClient.getPluginConfiguration(pluginId);
                view.querySelector('.storageEnabled').checked = config.Enabled;
                view.querySelector('#storageTokenHours').value = config.PlaybackTokenHours;
                showBucket(config.Buckets.length ? 0 : -1);
            } finally { loading.hide(); }
        });
        select.addEventListener('change', function () { saveBucket(); showBucket(Number(select.value)); });
        view.querySelector('.storageEnabled').addEventListener('change', updateVisibility);
        view.querySelector('#storageDelivery').addEventListener('change', updateVisibility);
        view.querySelector('.mountWithS3fs').addEventListener('change', updateVisibility);
        view.querySelector('.addBucket').addEventListener('click', function () {
            saveBucket();
            config.Buckets.push({ Name: 'S3 ' + (config.Buckets.length + 1), Endpoint: '', Region: 'us-east-1', Bucket: '', KeyPrefix: '', LocalPath: '', AccessKey: '', SecretKey: '', DeliveryMode: 'Worker', PublicBaseUrl: '', SigningSecret: '', MountWithS3fs: false, S3fsExecutable: '/usr/bin/s3fs', MountAllowOther: false, WorkerProjectDirectory: '', WorkerName: 'jellyfin-s3-delivery', CloudflareAccountId: '', CloudflareApiToken: '', AutoDeployWorker: false, AllowedOrigins: '*' });
            showBucket(config.Buckets.length - 1);
        });
        view.querySelector('.removeBucket').addEventListener('click', function () {
            if (selected >= 0) config.Buckets.splice(selected, 1);
            showBucket(config.Buckets.length ? 0 : -1);
        });
        view.querySelector('.generateSecret').addEventListener('click', function () {
            view.querySelector('#storageSigningSecret').value = [...crypto.getRandomValues(new Uint8Array(32))].map(v => v.toString(16).padStart(2, '0')).join('');
        });
        view.querySelector('.testBucket').addEventListener('click', async function () {
            saveBucket();
            loading.show();
            try {
                const response = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Plugins/S3Storage/Test'), data: JSON.stringify(config.Buckets[selected]), contentType: 'application/json', dataType: 'json' });
                view.querySelector('.testResult').textContent = response.Message;
            } catch (error) {
                view.querySelector('.testResult').textContent = '连接失败，请检查地址、凭据和存储桶权限。';
            } finally { loading.hide(); }
        });
        async function deploy(dryRun) {
            saveBucket(); loading.show();
            try {
                config.Enabled = view.querySelector('.storageEnabled').checked;
                config.PlaybackTokenHours = Number(view.querySelector('#storageTokenHours').value);
                let response;
                if (dryRun) response = await ApiClient.ajax({type: 'POST', url: ApiClient.getUrl('Plugins/S3Storage/Worker/Build'), data: JSON.stringify(config.Buckets[selected]), contentType: 'application/json', dataType: 'json'});
                else {
                    await ApiClient.updatePluginConfiguration(pluginId, config);
                    response = await ApiClient.ajax({type: 'POST', url: ApiClient.getUrl('Plugins/S3Storage/Worker/' + selected + '/Deploy'), dataType: 'json'});
                }
                view.querySelector('.testResult').textContent = response.Message;
            } catch (error) { view.querySelector('.testResult').textContent = 'Worker 操作失败，请检查项目目录、Node.js 和 Cloudflare 凭据。'; }
            finally { loading.hide(); }
        }
        view.querySelector('.buildWorker').addEventListener('click', () => deploy(true));
        view.querySelector('.deployWorker').addEventListener('click', () => deploy(false));
        view.querySelector('.refreshStorageStatus').addEventListener('click', async function () {
            try { const status = await ApiClient.ajax({type: 'GET', url: ApiClient.getUrl('Plugins/S3Storage/Status'), dataType: 'json'}); view.querySelector('.testResult').textContent = status[config.Buckets[selected].LocalPath] || '等待同步配置'; }
            catch (error) { toast('读取状态失败'); }
        });
        view.querySelector('form').addEventListener('submit', async function (event) {
            event.preventDefault();
            saveBucket();
            config.Enabled = view.querySelector('.storageEnabled').checked;
            config.PlaybackTokenHours = Number(view.querySelector('#storageTokenHours').value);
            loading.show();
            try { await ApiClient.updatePluginConfiguration(pluginId, config); toast('设置已保存'); }
            catch (error) { toast('保存失败，请检查配置。'); }
            finally { loading.hide(); }
        });
    };
});
