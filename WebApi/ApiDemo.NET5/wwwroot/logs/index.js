// Gets log records
logLoad();

function logLoad() {
    var input = $('#logSearchInput'), btn = $('#logMoreBtn'), info = $('#logMoreInfo'), tbl = $('#logListTbl');
    var p = parseInt(btn.attr('page')), searchText = $.trim(input.val());
    if (isNaN(p) || p < 1) p = 1;
    if (searchText) searchText = '?search=' + encodeURIComponent(searchText);
    input.val(''); btn.attr('page', p).hide();
    $.ajax({
        type: 'GET',
        url: '/api/log/exception/query/' + p + searchText,
        contentType: 'application/json',
        success: (data) => {
            //console.log(data);
            info.html('已加载' + (20 * (p > 1 ? p - 1 : 0) + data.rows.length) + '条记录（共' + data.records + '条）').show();
            if (data.records == 0) return;
            if (data.total > p) btn.attr('page', (p + 1)).show();
            if (searchText) tbl.html('');
            for (var i = 0; i < data.rows.length; i++) {
                var item = data.rows[i];
                var tpl = '<tr><td><span class="text-muted bg-info" style="cursor:pointer" onclick="$(\'#' + (item.id.replace('-', '')) + '\').toggle()">' + (item.path) + '</span><br><span class="text-danger bg-warning">' + (item.trace) + '</span></td><td class="text-danger vmiddle">' + (item.message) + '</td><td class="text-muted vmiddle">' + (item.time) + '</td><td class="vmiddle"><button type="button" class="btn btn-xs btn-danger" page="1" onclick="logDelete(this,\'' + (item.id) + '\')">删除</button></td></tr>';
                tpl += '<tr id="' + (item.id.replace('-', '')) + '" style="display:none"><td colspan="4" align="left"><pre>' + (item.content) + '</pre></td></tr>';
                //console.log(tpl);
                tbl.append($(tpl));
            }
        },
        error: (errMsg) => {
            console.log(errMsg);
        }
    });
}

function logSearch(e) {
    if (e.keyCode != 13) return;
    var btn = $('#logMoreBtn');
    btn.attr('page', '1').hide();
    logLoad();
}

function logDelete(btn, id) {
    var tr = $(btn).parent().parent(), tr1 = $('#' + id.replace('-', ''));
    $.ajax({
        type: 'DELETE',
        url: '/api/log/exception/delete/' + id,
        contentType: 'application/json',
        success: (data) => {
            //console.log(data);
            tr.remove(); tr1.remove();
        },
        error: (errMsg) => {
            console.log(errMsg);
        }
    });
}