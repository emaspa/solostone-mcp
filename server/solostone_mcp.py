#!/usr/bin/env python3
"""SoloStone MCP server (stdio, JSON-RPC 2.0, no dependencies).

Layer 1: engine workers (worker/, one process per engine worktree) for scenarios, sessions, seed search and fuzzing.
Layer 2: the game client's test-control endpoint (hook builds only), on the Linux lab install or the win11 VM.
"""
import base64
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import traceback
import uuid

HOME = os.path.expanduser('~')
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKER_SRC = os.path.join(REPO, 'worker')
CACHE = os.environ.get('SOLOSTONE_MCP_CACHE', os.path.join(HOME, '.cache', 'solostone-mcp'))
STATE = os.environ.get('SOLOSTONE_MCP_STATE', os.path.join(HOME, '.local', 'state', 'solostone-mcp'))
DEFAULT_ENGINE = os.environ.get('SOLOSTONE_MCP_ENGINE', os.path.join(HOME, 'solostone-merge'))
DOTNET = os.environ.get('SOLOSTONE_MCP_DOTNET') or shutil.which('dotnet') or os.path.join(HOME, '.dotnet', 'dotnet')
LAB = os.environ.get('SOLOSTONE_LAB', os.path.join(HOME, '.local', 'share', 'solostone-lab'))
VM_HOST = os.environ.get('SOLOSTONE_VM', 'win11')
os.makedirs(CACHE, exist_ok=True)
os.makedirs(os.path.join(STATE, 'findings'), exist_ok=True)


def log(*a):
    print(*a, file=sys.stderr, flush=True)


class ToolError(Exception):
    pass


# ---------------------------------------------------------------- engine workers
def git(wt, *args):
    return subprocess.run(['git', '-C', wt] + list(args), capture_output=True, text=True).stdout


def engine_key(wt):
    head = git(wt, 'rev-parse', 'HEAD').strip()
    if not head:
        raise ToolError('%s is not a git worktree' % wt)
    diff = git(wt, 'diff', 'HEAD', '--', 'engine/')
    untracked = git(wt, 'ls-files', '--others', '--exclude-standard', '--', 'engine/')
    h = hashlib.sha256()
    for part in (os.path.realpath(wt), head, diff, untracked):
        h.update(part.encode()); h.update(b'\0')
    for name in sorted(os.listdir(WORKER_SRC)):
        if name.endswith('.cs') or name.endswith('.csproj'):
            with open(os.path.join(WORKER_SRC, name), 'rb') as f:
                h.update(name.encode()); h.update(f.read())
    return h.hexdigest()[:16], head[:10], bool(diff.strip() or untracked.strip())


BUILD_LOCK = threading.Lock()


class Worker:
    def __init__(self, wt):
        self.wt = os.path.realpath(os.path.expanduser(wt))
        self.key, self.head, self.dirty = engine_key(self.wt)
        self.dir = os.path.join(CACHE, 'workers', self.key)
        self.lock = threading.Lock()
        self.proc = None
        self.seq = 0
        self.build()
        self.start()

    def build(self):
        dll = os.path.join(self.dir, 'out', 'EngineProof.dll')
        if os.path.exists(dll):
            return
        with BUILD_LOCK:
            src = os.path.join(self.dir, 'src')
            os.makedirs(src, exist_ok=True)
            for name in os.listdir(WORKER_SRC):
                if name.endswith('.cs') or name.endswith('.csproj'):
                    shutil.copy(os.path.join(WORKER_SRC, name), src)
            env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
            env['PATH'] = os.path.dirname(DOTNET) + ':' + env.get('PATH', '')
            r = subprocess.run([DOTNET, 'build', '-c', 'Release', '-nologo', '-v', 'q', '-p:EngineDir=' + self.wt,
                                '-o', os.path.join(self.dir, 'out'), os.path.join(src, 'EngineProof.csproj')],
                               capture_output=True, text=True, env=env)
            if r.returncode != 0 or not os.path.exists(dll):
                errors = '\n'.join(l for l in (r.stdout + r.stderr).splitlines() if ' error ' in l)[:4000]
                raise ToolError('worker build failed for %s:\n%s' % (self.wt, errors or (r.stdout + r.stderr)[-4000:]))

    def start(self):
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
        self.errlog = open(os.path.join(self.dir, 'worker.log'), 'a')
        self.proc = subprocess.Popen([DOTNET, os.path.join(self.dir, 'out', 'EngineProof.dll')], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=self.errlog, text=True, bufsize=1, env=env)

    def call(self, cmd, **args):
        with self.lock:
            if self.proc.poll() is not None:
                self.start()
            self.seq += 1
            req = dict(args, id=self.seq, cmd=cmd)
            self.proc.stdin.write(json.dumps(req) + '\n')
            self.proc.stdin.flush()
            line = self.proc.stdout.readline()
            if not line:
                raise ToolError('engine worker died (see %s/worker.log)' % self.dir)
            reply = json.loads(line)
            if not reply.get('ok'):
                raise ToolError(reply.get('error', 'worker error') + ('\n' + reply['trace'] if reply.get('trace') else ''))
            return reply['result']

    def describe(self):
        return {'worktree': self.wt, 'head': self.head, 'dirty': self.dirty, 'key': self.key,
                'branch': git(self.wt, 'rev-parse', '--abbrev-ref', 'HEAD').strip()}


WORKERS = {}


def worker(wt=None):
    wt = os.path.realpath(os.path.expanduser(wt or DEFAULT_ENGINE))
    key = engine_key(wt)[0]
    w = WORKERS.get(wt)
    if w is None or w.key != key:
        if w is not None and w.proc:
            w.proc.kill()
        w = WORKERS[wt] = Worker(wt)
    return w


SESSIONS = {}  # public id -> (worker, worker session id)


def session(sid):
    if sid is None and SESSIONS:
        sid = next(reversed(SESSIONS))  # the newest session
    if sid not in SESSIONS:
        raise ToolError('no session %s; open: %s' % (sid, ', '.join(SESSIONS) or 'none'))
    return SESSIONS[sid]


def load_scenario(scenario=None, path=None):
    if path:
        p = os.path.expanduser(path)
        if not os.path.isabs(p) and not os.path.exists(p):
            p = os.path.join(REPO, 'scenarios', p)
        with open(p) as f:
            return json.load(f)
    if isinstance(scenario, str):
        return json.loads(scenario)
    if not scenario:
        raise ToolError('give scenario (object) or path')
    return scenario


def failures(res):
    out = []
    for st in res.get('steps', []):
        if not st.get('ok'):
            out.append('step %s: %s' % (json.dumps(st.get('step')), st.get('error') or st.get('exception')))
        for e in (st.get('expect') or {}).get('results', []):
            if not e['pass']:
                out.append('after step %s: %s' % (json.dumps(st.get('step')), e.get('why')))
    for e in res.get('expect', {}).get('results', []):
        if not e['pass']:
            out.append('final: ' + str(e.get('why')))
    return out


# ---------------------------------------------------------------- tools: engine
def t_engine_info(worktree=None):
    w = worker(worktree)
    info = w.describe()
    info['cards'] = w.call('ping')['cards']
    info['default'] = DEFAULT_ENGINE
    return info


def t_card_search(query, limit=15, text=False, collectible=False, worktree=None):
    return worker(worktree).call('cards', query=query, limit=limit, text=text, collectible=collectible)


def grep_steps(steps, grep):
    if not grep:
        return steps
    rx = re.compile(grep, re.I)
    for st in steps or []:
        if isinstance(st, dict) and isinstance(st.get('history'), list):
            st['history'] = [l for l in st['history'] if rx.search(l)]
    return steps


def t_scenario_run(scenario=None, path=None, engines=None, verbose=False, keep=False, grep=None, state=False):
    sc = load_scenario(scenario, path)
    engines = engines or [DEFAULT_ENGINE]
    out = {'name': sc.get('name'), 'results': []}
    for wt in engines:
        w = worker(wt)
        sc2 = dict(sc, verbose=verbose or bool(grep))  # the worker drops step history unless verbose
        res = w.call('scenario', scenario=sc2, keep=keep)
        entry = {'engine': w.describe(), 'pass': res['pass'], 'failures': failures(res)}
        if keep and res.get('session'):
            pid = 's%d' % (len(SESSIONS) + 1)
            SESSIONS[pid] = (w, res['session'])
            entry['session'] = pid
        if verbose or len(engines) == 1:
            entry['steps'] = grep_steps(res['steps'], grep)
            if state:
                entry['state'] = res.get('state')
        out['results'].append(entry)
    if len(engines) > 1:
        out['summary'] = ['%s %s@%s%s' % ('PASS' if r['pass'] else 'FAIL', os.path.basename(r['engine']['worktree']),
                                          r['engine']['head'], '+dirty' if r['engine']['dirty'] else '') for r in out['results']]
    return out


def t_game_new(setup=None, worktree=None, seed=None):
    w = worker(worktree)
    args = {'setup': setup or {}}
    if seed is not None:
        args['seed'] = seed
    res = w.call('new', **args)
    pid = 's%d' % (len(SESSIONS) + 1)
    SESSIONS[pid] = (w, res['session'])
    res['session'] = pid
    res['engine'] = w.describe()
    return res


def scall(sid, cmd, **args):
    w, inner = session(sid)
    return w.call(cmd, session=inner, **args)


def t_game_state(session=None, full=False):
    return scall(session, 'state', full=full)


def t_game_entity(ref, session=None):
    return scall(session, 'entity', ref=ref)


def t_game_options(session=None, by=None):
    return scall(session, 'options', **({'by': by} if by else {}))


def t_game_act(steps, session=None, grep=None, state=True):
    if isinstance(steps, dict):
        steps = [steps]
    res = scall(session, 'act', steps=steps)
    if isinstance(res, dict):
        grep_steps(res.get('steps'), grep)
        if not state:
            res.pop('state', None)
    return res


def t_game_history(session=None, range='step', start=None, end=None, grep=None, limit=300):
    args = {'range': range, 'limit': limit}
    if start is not None:
        args['from'] = start
    if end is not None:
        args['to'] = end
    if grep:
        args['grep'] = grep
    return scall(session, 'history', **args)


def t_game_assert(expect, session=None):
    return scall(session, 'assert', expect=expect)


def t_game_diag(session=None):
    return scall(session, 'diag')


def t_game_checkpoint(name='default', session=None):
    return scall(session, 'checkpoint', name=name)


def t_game_restore(name='default', session=None):
    return scall(session, 'restore', name=name)


def t_game_close(session=None):
    w, inner = SESSIONS.pop(session) if session in SESSIONS else (None, None)
    if w:
        w.call('close', session=inner)
    return {'closed': session, 'open': list(SESSIONS)}


def t_seed_search(scenario=None, path=None, worktree=None, start=1, count=300, want=1):
    sc = load_scenario(scenario, path)
    return worker(worktree).call('seed_search', scenario=sc, **{'from': start, 'count': count, 'want': want})


# ---------------------------------------------------------------- fuzzing and findings
def finding_path(fid):
    return os.path.join(STATE, 'findings', fid + '.json')


def t_fuzz_run(worktree=None, games=60, seed=None, seconds=200, classes=None, format='wild', max_actions=400, compare=None):
    w = worker(worktree)
    seed = seed if seed is not None else int(time.time()) % 1000000
    res = w.call('fuzz', games=games, seed=seed, seconds=seconds, classes=classes or [], format=format, maxActions=max_actions)
    saved = []
    for f in res['findings']:
        sig = f['invariant'] + '|' + re.sub(r'#\d+|\d+', 'N', f.get('detail') or '')
        fid = f['invariant'].split('-')[0] + '-' + hashlib.sha1(sig.encode()).hexdigest()[:8]
        path = finding_path(fid)
        record = {'id': fid, 'signature': sig, 'engine': w.describe(), 'found': time.strftime('%Y-%m-%d %H:%M'), 'finding': f}
        if os.path.exists(path):
            with open(path) as fh:
                old = json.load(fh)
            record['seen'] = old.get('seen', 1) + 1
            record['found'] = old.get('found')
            record['status'] = old.get('status', 'open')
        else:
            record['seen'] = 1
            record['status'] = 'open'
        with open(path, 'w') as fh:
            json.dump(record, fh, indent=1)
        saved.append({'id': fid, 'invariant': f['invariant'], 'seed': f.get('seed'), 'classes': f.get('classes'),
                      'detail': (f.get('detail') or '')[:300], 'new': record['seen'] == 1})
    out = {'engine': w.describe(), 'games': res['games'], 'actions': res['actions'], 'turns': res['turns'],
           'firstSeed': res['firstSeed'], 'findings': saved}
    if compare:
        out['compare'] = {}
        for f in saved:
            out['compare'][f['id']] = t_finding_verify(f['id'], engines=compare)['results']
    return out


def t_finding_list(status=None):
    rows = []
    for p in sorted(glob.glob(os.path.join(STATE, 'findings', '*.json')), key=os.path.getmtime, reverse=True):
        with open(p) as f:
            r = json.load(f)
        if status and r.get('status') != status:
            continue
        f = r['finding']
        rows.append({'id': r['id'], 'status': r.get('status'), 'seen': r.get('seen'), 'found': r.get('found'),
                     'invariant': f['invariant'], 'classes': f.get('classes'), 'seed': f.get('seed'),
                     'engine': '%s@%s' % (os.path.basename(r['engine']['worktree']), r['engine']['head']),
                     'detail': (f.get('detail') or '')[:240]})
    return rows


def t_finding_get(id):
    with open(finding_path(id)) as f:
        return json.load(f)


def t_finding_set(id, status, note=None):
    r = t_finding_get(id)
    r['status'] = status
    if note:
        r.setdefault('notes', []).append({'at': time.strftime('%Y-%m-%d %H:%M'), 'note': note})
    with open(finding_path(id), 'w') as f:
        json.dump(r, f, indent=1)
    return {'id': id, 'status': status}


def t_finding_verify(id, engines=None, keep=False):
    r = t_finding_get(id)
    f = r['finding']
    engines = engines or [DEFAULT_ENGINE]
    out = {'id': id, 'invariant': f['invariant'], 'results': []}
    for wt in engines:
        w = worker(wt)
        if 'picks' not in f:
            out['results'].append({'engine': w.describe(), 'error': 'finding has no recorded picks'})
            continue
        res = w.call('replay', seed=f['seed'], classes=f['classes'], picks=f['picks'], standard=f.get('standard', False), keep=keep)
        same = [x for x in res['findings'] if x['invariant'] == f['invariant']]
        entry = {'engine': w.describe(), 'reproduces': bool(same), 'findings': [(x['invariant'], (x.get('detail') or '')[:200]) for x in res['findings']],
                 'actions': res['actions']}
        if keep and res.get('session'):
            pid = 's%d' % (len(SESSIONS) + 1)
            SESSIONS[pid] = (w, res['session'])
            entry['session'] = pid
        out['results'].append(entry)
    return out


def t_finding_replay(id, stop_at=None, worktree=None):
    """Replays a finding into a live session, stopping before action stop_at (default: just before the failing action)."""
    r = t_finding_get(id)
    f = r['finding']
    picks = f['picks']
    stop = stop_at if stop_at is not None else max(0, (f.get('action') or len(picks)) - 0)
    w = worker(worktree)
    res = w.call('replay', seed=f['seed'], classes=f['classes'], picks=picks[:stop], standard=f.get('standard', False), keep=True)
    pid = 's%d' % (len(SESSIONS) + 1)
    SESSIONS[pid] = (w, res['session'])
    return {'session': pid, 'replayedActions': res['actions'], 'nextPick': picks[stop] if stop < len(picks) else None,
            'state': res['state'], 'hint': 'game_options then game_act {"option": nextPick} re-runs the failing action'}


# ---------------------------------------------------------------- journals (real installs, read-only)
def install_roots():
    roots = {}
    for name, base in (('desk', os.path.join(HOME, '.local', 'share', 'SoloStone', 'prefix')),
                       ('lab', os.path.join(LAB, 'data', 'SoloStone', 'prefix'))):
        hits = glob.glob(os.path.join(base, 'drive_c', 'users', '*', 'AppData', 'Local', 'SoloStone'))
        hits = [h for h in hits if os.path.isdir(os.path.join(h, 'reports'))]
        if hits:
            roots[name] = hits[0]
    return roots


def journal_dir(install):
    roots = install_roots()
    if install not in roots:
        raise ToolError('no install %s; found %s' % (install, ', '.join(roots) or 'none'))
    return os.path.join(roots[install], 'reports', 'playtest')


def t_journal_list(install='desk', limit=15):
    d = journal_dir(install)
    rows = []
    for p in sorted(glob.glob(os.path.join(d, '*.jsonl')), key=os.path.getmtime, reverse=True)[:limit]:
        head, result = None, None
        with open(p) as f:
            for line in f:
                if head is None and '"header"' in line[:40]:
                    head = json.loads(line)
                if line.startswith('{"type":"result"'):
                    result = json.loads(line)
        rows.append({'match': os.path.basename(p)[:-6], 'modified': time.strftime('%Y-%m-%d %H:%M', time.localtime(os.path.getmtime(p))),
                     'npc': head and head.get('npcName'), 'humanHero': head and (head.get('humanDeck') or {}).get('hero'),
                     'result': result and result.get('result'), 'turns': result and result.get('turns')})
    return rows


def t_journal_read(match=None, install='desk', types=None, grep=None, turn_from=None, turn_to=None, limit=200, offset=0):
    d = journal_dir(install)
    if not match:
        files = sorted(glob.glob(os.path.join(d, '*.jsonl')), key=os.path.getmtime, reverse=True)
        if not files:
            raise ToolError('no journals')
        path = files[0]
    else:
        hits = glob.glob(os.path.join(d, match + '*.jsonl'))
        if not hits:
            raise ToolError('no journal %s' % match)
        path = hits[0]
    types = types or ['header', 'events', 'human-action', 'decision', 'choice-open', 'mulligan', 'result']
    rx = re.compile(grep, re.I) if grep else None
    out, n = [], 0
    with open(path) as f:
        for line in f:
            o = json.loads(line)
            t = o.get('type')
            if t not in types:
                continue
            if t == 'events':
                for ev in o.get('lines', []):
                    m = re.search(r' t=(\d+) ', ev)
                    turn = int(m.group(1)) if m else None
                    if turn_from is not None and turn is not None and turn < turn_from:
                        continue
                    if turn_to is not None and turn is not None and turn > turn_to:
                        continue
                    if rx and not rx.search(ev):
                        continue
                    n += 1
                    if n > offset and len(out) < limit:
                        out.append(ev)
                continue
            turn = o.get('turn') or o.get('stateTurn')
            if turn_from is not None and turn is not None and turn < turn_from:
                continue
            if turn_to is not None and turn is not None and turn > turn_to:
                continue
            if t == 'header':
                o = {k: o[k] for k in ('type', 'matchId', 'npcName', 'startedUtc', 'seed', 'firstPlayer') if k in o} | {
                    'humanDeck': (o.get('humanDeck') or {}).get('cards'), 'npcDeck': (o.get('npcDeck') or {}).get('cards'),
                    'humanHero': (o.get('humanDeck') or {}).get('hero'), 'npcHero': (o.get('npcDeck') or {}).get('hero')}
            elif t == 'decision':
                o = {k: o.get(k) for k in ('type', 'turn', 'action', 'driverKind', 'value', 'fallback')}
            elif t == 'fx':
                o = {k: o.get(k) for k in ('type', 'fxSequence', 'event', 'spellName', 'stateName', 'targets')}
            s = json.dumps(o)
            if rx and not rx.search(s):
                continue
            n += 1
            if n > offset and len(out) < limit:
                out.append(s)
    return {'journal': path, 'shown': len(out), 'matched': n, 'lines': out}


# ---------------------------------------------------------------- live client (test-control)
LIVE = {'conn': None}


def lab_env():
    return dict(os.environ, XDG_DATA_HOME=os.path.join(LAB, 'data'), XDG_CONFIG_HOME=os.path.join(LAB, 'config'),
                XDG_STATE_HOME=os.path.join(LAB, 'state'))


def linux_game_pid(prefix_hint):
    """A process of the running game inside the launcher's network namespace."""
    own = os.readlink('/proc/self/ns/net')
    for pid in os.listdir('/proc'):
        if not pid.isdigit():
            continue
        try:
            with open('/proc/%s/cmdline' % pid, 'rb') as f:
                cmd = f.read().replace(b'\0', b' ').decode(errors='replace')
            if 'Hearthstone.exe' not in cmd:
                continue
            with open('/proc/%s/environ' % pid, 'rb') as f:
                env = f.read().decode(errors='replace')
            if prefix_hint and prefix_hint not in env:
                continue
            if os.readlink('/proc/%s/ns/net' % pid) == own:
                continue
            return int(pid)
        except (OSError, PermissionError):
            continue
    return None


class LiveConn:
    """One authenticated test-control connection. The transport is a child process whose stdio is the TCP stream:
    nsenter+socat into the Linux launcher's network namespace, or ssh -W to the VM."""

    def __init__(self, target, root_dir, argv, port, run_id, fetch):
        self.target, self.root, self.port, self.run_id, self.fetch = target, root_dir, port, run_id, fetch
        self.proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        self.buf = b''
        self.lock = threading.Lock()
        self.seq = 0
        # The plugin remembers request ids for the whole launch and refuses a reused id with a new body, so ids carry a per-connection tag.
        self.tag = os.urandom(3).hex()
        self.pending = []

    def send(self, obj):
        try:
            self.proc.stdin.write((json.dumps(obj, separators=(',', ':')) + '\n').encode())
            self.proc.stdin.flush()
        except BrokenPipeError:
            raise ToolError('test-control connection lost (the endpoint takes one client; another one may have connected); call live_connect again')

    def read_line(self, deadline):
        import select
        while b'\n' not in self.buf:
            left = deadline - time.time()
            if left <= 0:
                return None
            r, _, _ = select.select([self.proc.stdout], [], [], left)
            if not r:
                return None
            chunk = os.read(self.proc.stdout.fileno(), 65536)
            if not chunk:
                err = self.proc.stderr.read().decode(errors='replace') if self.proc.poll() is not None else ''
                raise ToolError('test-control connection closed' + (': ' + err.strip() if err else ''))
            self.buf += chunk
        line, self.buf = self.buf.split(b'\n', 1)
        return json.loads(line)

    def request(self, cmd, args, wait=15.0, until=None):
        with self.lock:
            self.seq += 1
            rid = 'mcp-%s-%d' % (self.tag, self.seq)
            req = {'v': 1, 'id': rid, 'cmd': cmd}
            req.update(args or {})
            self.send(req)
            deadline = time.time() + wait
            replies = []
            while True:
                msg = self.read_line(deadline)
                if msg is None:
                    break
                if msg.get('id') != rid:
                    continue
                replies.append(msg)
                phase = msg.get('phase')
                if until and phase in until:
                    break
                if not until and phase not in ('accepted', 'submitted', 'queued', 'pending', 'started'):
                    break
            return replies

    def close(self):
        try:
            self.proc.kill()
        except Exception:
            pass


def ensure_lo(pid):
    subprocess.run(['nsenter', '-t', str(pid), '-U', '-n', '--preserve-credentials', '--keep-caps', 'ip', 'link', 'set', 'lo', 'up'],
                   capture_output=True, text=True, check=True)


def read_text(target, path):
    if target == 'vm':
        r = subprocess.run(['ssh', VM_HOST, 'powershell', '-NoProfile', '-Command', 'Get-Content -Raw "%s"' % path],
                           capture_output=True, text=True, timeout=30)
        if r.returncode != 0:
            raise ToolError('cannot read %s on %s: %s' % (path, VM_HOST, r.stderr.strip()))
        return r.stdout
    with open(path, encoding='utf-8-sig') as f:
        return f.read()


def write_text(target, path, text):
    if target == 'vm':
        enc = base64.b64encode(text.encode()).decode()
        cmd = "[IO.File]::WriteAllBytes('%s', [Convert]::FromBase64String('%s'))" % (path, enc)
        r = subprocess.run(['ssh', VM_HOST, 'powershell', '-NoProfile', '-Command', cmd], capture_output=True, text=True, timeout=30)
        if r.returncode != 0:
            raise ToolError('cannot write %s on %s: %s' % (path, VM_HOST, r.stderr.strip()))
        return
    with open(path, 'w', newline='\n') as f:
        f.write(text)


def vm_root():
    r = subprocess.run(['ssh', VM_HOST, 'powershell', '-NoProfile', '-Command', '$env:LOCALAPPDATA'], capture_output=True, text=True, timeout=30)
    if r.returncode != 0:
        raise ToolError('ssh %s failed: %s' % (VM_HOST, r.stderr.strip()))
    return r.stdout.strip() + '\\SoloStone'


def t_live_connect(target='lab', lane=0, wait=20):
    """Enable test control for the running game and authenticate. target: lab (Linux lab install), desk (the user's
    own Linux install; needs a hook build there too) or vm (win11 over ssh)."""
    if LIVE['conn']:
        LIVE['conn'].close()
        LIVE['conn'] = None
    port = 18765 + int(lane)
    if target == 'vm':
        root = vm_root()
        sep = '\\'
    else:
        roots = install_roots()
        if target not in roots:
            raise ToolError('no %s install found (have: %s). Build and start the lab with lab.sh.' % (target, ', '.join(roots) or 'none'))
        root = roots[target]
        sep = '/'
    reports = root + sep + 'reports'
    loaded = read_text(target, reports + sep + 'plugin-loaded.txt').splitlines()
    run_id = loaded[0].strip() if loaded else ''
    if not re.match(r'^[0-9a-f-]{36}$', run_id):
        raise ToolError('plugin-loaded.txt has no run id; is the game running? (%r)' % loaded[:2])
    token = uuid.uuid4().hex + uuid.uuid4().hex
    write_text(target, reports + sep + 'enable-test-control', 'runId=%s\ntoken=%s\n' % (run_id, token))
    if target == 'vm':
        argv = ['ssh', '-o', 'BatchMode=yes', '-W', '127.0.0.1:%d' % port, VM_HOST]
    else:
        pid = linux_game_pid('WINEPREFIX=' + root.split('/drive_c/')[0])
        if pid is None:
            raise ToolError('the game is not running in a launcher network namespace (start it with lab.sh play)')
        ensure_lo(pid)
        argv = ['nsenter', '-t', str(pid), '-U', '-n', '--preserve-credentials', 'socat', 'STDIO', 'TCP:127.0.0.1:%d' % port]
    deadline = time.time() + wait
    last = None
    while time.time() < deadline:
        conn = LiveConn(target, root, argv, port, run_id, None)
        try:
            conn.send({'v': 1, 'id': 'hello', 'cmd': 'hello', 'runId': run_id, 'token': token})
            msg = conn.read_line(time.time() + 3)
            if msg and msg.get('phase') != 'rejected':
                LIVE['conn'] = conn
                LIVE['target'] = target
                LIVE['reports'] = reports
                LIVE['sep'] = sep
                return {'connected': target, 'runId': run_id, 'port': port, 'hello': msg}
            last = msg
        except ToolError as e:
            last = str(e)
        conn.close()
        time.sleep(1.5)  # the plugin polls the flag file once a second
    raise ToolError('no test-control answer within %ss: %s. A release build has no endpoint; the lab needs a --hooks build.' % (wait, last))


def live():
    if not LIVE['conn']:
        raise ToolError('not connected; call live_connect first')
    return LIVE['conn']


def t_live_call(cmd, args=None, wait=15, until=None):
    """Raw test-control request. Fields are flat (strings, ints, int arrays). See plugin/TestControlProtocol.cs for each command."""
    replies = live().request(cmd, args or {}, wait=wait, until=until)
    if not replies:
        return {'cmd': cmd, 'timeout': wait}
    return replies[-1] if len(replies) == 1 else {'phases': [r.get('phase') for r in replies], 'final': replies[-1]}


def t_live_state(raw=False):
    r = t_live_call('state')
    if raw:
        return r
    d = data_of(r)
    e = d.get('engine') or {}
    def side(p):
        p = p or {}
        return {'hp': p.get('health'), 'armor': p.get('armor'), 'mana': p.get('mana'), 'deck': p.get('deckCount'),
                'hand': [(c.get('id'), c.get('cardId')) for c in p.get('hand', [])] or p.get('handCount'),
                'board': [(c.get('id'), c.get('cardId')) for c in p.get('board', [])]}
    return {'scene': d.get('scene'), 'matchId': r.get('matchId') if isinstance(r, dict) else None, 'responseMode': d.get('responseMode'),
            'optionsId': d.get('humanOptionsId'), 'choiceId': d.get('choiceId'), 'inputEnabled': d.get('inputEnabled'),
            'step': e.get('step'), 'turn': e.get('turn'), 'current': e.get('currentPlayerId'),
            'human': side(e.get('human')), 'opponent': side(e.get('opponent')), 'differences': d.get('differences')}


def t_live_play(entity=None, target=0, position=0, sub=-1, end_turn=False, settle=6, raw_fx=False):
    """Plays one option by its source entity (or ends the turn), waits for the client to settle and returns the fx it caused."""
    rows, cursor = fx_rows(LIVE_FX_CURSOR[0], 100000)
    LIVE_FX_CURSOR[0] = cursor
    o = data_of(t_live_options())
    if end_turn:
        act = t_live_end_turn(o['optionsId'])
    else:
        row = next((x for x in o['rows'] if x['main']['source'] == entity), None)
        if row is None:
            raise ToolError('no option with source %s; sources: %s' % (entity, [(x['index'], x['type'], x['main']['source']) for x in o['rows']]))
        if not position:
            position = next((p['positions'][0] for p in row['main']['permitted'] if p['target'] == target and p.get('positions')), 0)
        act = t_live_act(row['index'], o['optionsId'], target=target, sub=sub, position=position)
    time.sleep(settle)
    rows, cursor = fx_rows(cursor, 100000)
    LIVE_FX_CURSOR[0] = cursor
    fin = data_of(act)
    return {'phase': (act.get('final') or act).get('phase') if isinstance(act, dict) else None, 'error': (act or {}).get('error'),
            'fx': rows if raw_fx else [r for r in rows if not fx_noise(r)]}


LIVE_FX_CURSOR = [0]


def match_id():
    st = live().request('state', {}, wait=10)
    if not st:
        raise ToolError('no state reply')
    mid = st[-1].get('matchId')
    if not mid:
        raise ToolError('no match running (matchId empty)')
    return mid, st[-1]


def t_live_options():  # noqa
    mid, _ = match_id()
    return t_live_call('options', {'matchId': mid})


def t_live_act(index, options_id, target=0, sub=-1, position=0, wait=30):
    mid, _ = match_id()
    r = live().request('act', {'matchId': mid, 'optionsId': options_id, 'index': index, 'target': target, 'sub': sub, 'position': position}, wait=wait)
    return r[-1] if r else {'timeout': wait}


def t_live_end_turn(options_id, wait=30):
    mid, _ = match_id()
    r = live().request('end-turn', {'matchId': mid, 'optionsId': options_id}, wait=wait)
    return r[-1] if r else {'timeout': wait}


def t_live_choose(choice_id, entity_id, click=False, wait=30):
    mid, _ = match_id()
    r = live().request('choice-click' if click else 'choose', {'matchId': mid, 'choiceId': choice_id, 'entityIds': [entity_id]}, wait=wait)
    return r[-1] if r else {'timeout': wait}


def t_live_wait_idle(after_action_id, timeout_ms=20000):
    mid, _ = match_id()
    r = live().request('wait-until-idle', {'matchId': mid, 'afterActionId': after_action_id, 'timeoutMs': timeout_ms}, wait=timeout_ms / 1000 + 5)
    return r[-1] if r else {'timeout': timeout_ms}


def t_live_entities(entity_ids):
    mid, _ = match_id()
    return t_live_call('entity', {'matchId': mid, 'entityIds': list(entity_ids)})


FX_KEEP = ['sequence', 'event', 'blockType', 'sourceEntity', 'cardId', 'effectCardId', 'effectIndex', 'subOption', 'triggerKeyword',
           'controllerType', 'effectPresent', 'prefab', 'spellName', 'stateName', 'result', 'error', 'targets']


def fx_rows(after, limit=100):
    """Telemetry rows after a cursor, parsed from the plugin's TSV, empty fields dropped."""
    out, last = [], after
    while True:
        d = data_of(t_live_call('fx', {'after': last, 'limit': min(limit, 100)}))
        hdr = d['header'].split('\t')
        recs = d.get('records') or []
        for line in recs:
            v = dict(zip(hdr, line.split('\t')))
            last = int(v['sequence'])
            out.append({k: v[k] for k in FX_KEEP if v.get(k) not in (None, 'null', '')})
        if len(recs) < 100 or len(out) >= limit:
            return out, last


def fx_noise(r):
    # Spell state churn: keep only activations, which are what the player sees.
    return r['event'].startswith('Spell.') and r['event'] != 'Spell.ActivateState'


def t_live_fx(after=0, limit=100, raw=False):
    rows, last = fx_rows(after, limit)
    if not raw:
        rows = [r for r in rows if not fx_noise(r)]
    return {'next': last, 'rows': rows}


def data_of(reply):
    if isinstance(reply, dict) and 'final' in reply:
        reply = reply['final']
    return reply.get('data', reply) if isinstance(reply, dict) else reply


def t_live_ui(all=False, raw=False):
    r = t_live_call('ui', {'all': 1} if all else {})
    if raw:
        return r
    d = data_of(r)
    els = [{k: e.get(k) for k in ('id', 'label', 'type')} | ({} if e.get('enabled', True) else {'disabled': True})
           for e in d.get('elements', []) if all or e.get('hit')]
    return {'scene': d.get('scene'), 'screen': d.get('screen'), 'idle': d.get('idle'), 'dialogs': d.get('dialogs'), 'elements': els}


def t_live_press(element, snapshot=None, timeout_ms=5000):
    args = {'element': element, 'timeoutMs': timeout_ms}
    if snapshot:
        args['snapshot'] = snapshot
    return t_live_call('press', args, wait=timeout_ms / 1000 + 10)


def fetch_file(remote):
    """Brings a file from the live target to a local path."""
    if LIVE['target'] == 'vm':
        local = os.path.join(CACHE, 'captures', os.path.basename(remote.replace('\\', '/')))
        os.makedirs(os.path.dirname(local), exist_ok=True)
        r = subprocess.run(['scp', '-q', '%s:%s' % (VM_HOST, remote.replace('\\', '/')), local], capture_output=True, text=True, timeout=60)
        if r.returncode != 0:
            raise ToolError('scp failed: ' + r.stderr.strip())
        return local
    return remote


def t_live_screenshot():
    """The game's own rendered frame (Unity ScreenCapture inside the game process; never the desktop)."""
    replies = live().request('screenshot', {}, wait=20, until=('settled', 'rejected', 'failed', 'completed'))
    final = replies[-1] if replies else None
    if not final:
        raise ToolError('no screenshot reply')
    data = final.get('data') or {}
    detail = data.get('detail') or data
    cid = detail.get('captureId') or data.get('captureId')
    path = detail.get('path') or data.get('path')
    if not path and cid:
        sep = LIVE['sep']
        cands = [LIVE['reports'] + sep + 'control' + sep + cid + '.png']
        if LIVE['target'] != 'vm':
            cands += glob.glob(os.path.join(os.path.dirname(LIVE['reports']), '**', cid + '.png'), recursive=True)
        for c in cands:
            if LIVE['target'] == 'vm' or os.path.exists(c):
                path = c
                break
    if not path:
        return {'reply': final, 'note': 'no capture path in the reply'}
    for _ in range(40):
        if LIVE['target'] == 'vm' or (os.path.exists(path) and os.path.getsize(path) > 0):
            break
        time.sleep(0.25)
    local = fetch_file(path)
    with open(local, 'rb') as f:
        png = f.read()
    return {'__image__': base64.b64encode(png).decode(), 'path': local, 'reply': {'phase': final.get('phase'), 'frame': final.get('frame')}}


def t_live_logs(which='unity', lines=80, grep=None):
    sep = LIVE.get('sep', '/')
    reports = LIVE.get('reports')
    if not reports:
        roots = install_roots()
        reports = roots.get('lab', roots.get('desk', '')) + '/reports'
        sep = '/'
    name = {'unity': 'Unity.log', 'wine': 'wine.log'}.get(which, which)
    text = read_text(LIVE.get('target', 'lab'), reports + sep + name)
    rows = text.splitlines()
    if grep:
        rx = re.compile(grep, re.I)
        rows = [r for r in rows if rx.search(r)]
    return {'file': reports + sep + name, 'lines': rows[-lines:]}


def t_live_disconnect():
    if LIVE['conn']:
        LIVE['conn'].close()
    LIVE['conn'] = None
    return {'disconnected': True}


# ---------------------------------------------------------------- tool table
S = lambda **p: p  # noqa: E731
STR = {'type': 'string'}
INT = {'type': 'integer'}
BOOL = {'type': 'boolean'}
OBJ = {'type': 'object'}
ARR = {'type': 'array'}
ENGINES = {'type': 'array', 'items': STR, 'description': 'Engine worktrees, e.g. ["~/solostone-merge", "~/solostone-secret"]. Default: ' + DEFAULT_ENGINE}
WT = {'type': 'string', 'description': 'Engine worktree path (default %s)' % DEFAULT_ENGINE}
SID = {'type': 'string', 'description': 'Session id from game_new / scenario_run keep / finding_replay (optional when one is open)'}

SCENARIO_DOC = '''Scenario JSON: {"name", "setup": {"seed", "first": "me"|"opp", "format": "wild"|"standard",
 "me"/"opp": {"class", "hero", "deck": [ids], "deckTop": [...], "hand": [...], "handOnly": bool, "board": [...], "secrets": [...],
   "weapon", "mana", "health", "armor"}},  -- "me" is Player1 (the human seat), "opp" Player2 (the NPC).
   hand/board/secrets/deckTop entries are a card id or name, or {"card", "as": "alias", "ready": bool, "damage": n, "tags": {TAG: v}}.
   Board minions are ready to attack unless "ready": false. Secrets are live on the opponent's turn.
 "steps": [step...], "expect": [expectation...]}
Steps (optional "by": me|opp, default current player or whoever owes a choice; "as": alias; "expect": [...] checked after the step):
 {"play": ref, "target": ref, "pos": n, "choose": n} {"attack": ref, "target": ref} {"power": true, "target": ref} {"trade": ref}
 {"activate": ref} {"choose": cardId|name|entityId|index|{"not": id}} {"end_turn": true} {"concede": true} {"option": index}
 forced effects outside the rules: {"cast": id, "for": me|opp, "creator": ref} (a spell cast for a player, as by Faceless Enigma or Yogg)
 {"summon": id, "for", "pos", "ready"} {"give": id, "for", "zone": hand|deck|secret} {"set": ref, "tags": {..}, "damage": n}
Refs: "@alias", entity id, "me.hero", "opp.power", "me.weapon", "opp.board[0]", "me.hand:EX1_611", "opp.secret:Freezing Trap", or a bare id/name
 (acting player's hand, board, secrets, then the opponent's board/secrets).
Expectations: {"entity": ref, "zone", "owner", "health", "damage", "atk", "armor", "exhausted", "card", "tags": {TAG: v}, "exists": false}
 {"zone": "opp.board", "count", "has": [ids|@alias], "hasNot": [...]} {"history": "step"|"full", "contains": [regex], "none": [regex], "order": [regex...]}
 {"current": me|opp} {"state": RUNNING|COMPLETE} {"winner": me|opp|none} {"choice": me|opp, "offersInclude": [...], "offersExclude": [...]}
 {"mana": me|opp, "remaining", "base"}
History lines look like: "BLOCK ATTACK src=Violet Spellwing#69 tgt=Rexxar#4", "  TAG Violet Spellwing#69 ATTACKING=1", "  TAG Game PROPOSED_ATTACKER=69",
 "BLOCK TRIGGER src=Freezing Trap#71", "FULL EX1_611#71 (...) zone=SETASIDE", "META DAMAGE data=1 info=...", "END".'''

TOOLS = [
    ('engine_info', t_engine_info, 'Build (cached) and describe the engine worker for a worktree: branch, HEAD, dirty flag, card count.', S(worktree=WT), []),
    ('card_search', t_card_search, 'Find cards by id or name (or text with text=true). Returns ids, cost, stats and text.',
     S(query=STR, limit=INT, text=BOOL, collectible=BOOL, worktree=WT), ['query']),
    ('scenario_run', t_scenario_run, 'Run a scenario on one or more engine worktrees and report pass/fail per engine. Use it to reproduce a bug '
     '(should FAIL on the buggy engine) and verify a fix (PASS on the fix worktree). keep=true leaves a session open at the end. '
     'path may name a file in the server\'s scenarios/ folder. grep keeps only matching history lines; state=true adds the final board.\n'
     + SCENARIO_DOC,
     S(scenario=OBJ, path=STR, engines=ENGINES, verbose=BOOL, keep=BOOL, grep=STR, state=BOOL), []),
    ('game_new', t_game_new, 'Start an engine game from a setup (same shape as a scenario "setup") and keep it as a session.\n' + SCENARIO_DOC,
     S(setup=OBJ, worktree=WT, seed=INT), []),
    ('game_state', t_game_state, 'Board, hands, secrets, mana, pending choice and aliases of a session. full=true adds deck order and graveyard.', S(session=SID, full=BOOL), []),
    ('game_entity', t_game_entity, 'All engine tags, text and enchantments of one entity (ref as in scenarios).', S(ref=STR, session=SID), ['ref']),
    ('game_options', t_game_options, 'The legal options for the player to act (or "by").', S(session=SID, by=STR), []),
    ('game_act', t_game_act, 'Apply steps (scenario step format) to a session (default: the newest). Each step reports ok/error and the power '
     'history it produced; grep keeps only matching history lines, state=false drops the board.',
     S(steps={'type': ['array', 'object']}, session=SID, grep=STR, state=BOOL), ['steps']),
    ('game_history', t_game_history, 'Power history lines (what the client would be sent): range "step" (last step) or "full", or start/end indices; grep is a regex.',
     S(session=SID, range=STR, start=INT, end=INT, grep=STR, limit=INT), []),
    ('game_assert', t_game_assert, 'Check expectations (scenario format) against a session now.', S(expect=ARR, session=SID), ['expect']),
    ('game_diag', t_game_diag, 'Engine-internal gates that make the engine refuse a task: autonomous turn, rewind, suspended effect, spell completion, pending decisions.', S(session=SID), []),
    ('game_checkpoint', t_game_checkpoint, 'Snapshot a session (engine SaveSnapshot).', S(name=STR, session=SID), []),
    ('game_restore', t_game_restore, 'Restore a session snapshot.', S(name=STR, session=SID), []),
    ('game_close', t_game_close, 'Close a session.', S(session=SID), []),
    ('seed_search', t_seed_search, 'Find seeds for which a scenario passes, for RNG-dependent setups (a Discover offer, a random target).',
     S(scenario=OBJ, path=STR, worktree=WT, start=INT, count=INT, want=INT), []),
    ('fuzz_run', t_fuzz_run, 'Play random engine games and check invariants: E1 exceptions, E2 offered options the engine refuses, E3 stuck or endless games, '
     'E4 impossible states (8 minions, dead minion alive, two copies of a secret...), E6 non-determinism, E8 attacks sent to the client as hits that never happen. '
     'Findings are saved (finding_list). compare=[worktrees] replays each finding there.',
     S(worktree=WT, games=INT, seed=INT, seconds=INT, classes={'type': 'array', 'items': STR}, format=STR, max_actions=INT, compare=ENGINES), []),
    ('finding_list', t_finding_list, 'Saved fuzz findings, newest first. status: open|fixed|wontfix|duplicate.', S(status=STR), []),
    ('finding_get', t_finding_get, 'One finding with its recorded picks and history tail.', S(id=STR), ['id']),
    ('finding_set', t_finding_set, 'Set a finding status (open, fixed, wontfix, duplicate, upstream) with an optional note.', S(id=STR, status=STR, note=STR), ['id', 'status']),
    ('finding_verify', t_finding_verify, 'Replay a finding on engine worktrees: does it still reproduce?', S(id=STR, engines=ENGINES, keep=BOOL), ['id']),
    ('finding_replay', t_finding_replay, 'Replay a finding into a session, stopped just before the failing action, to inspect it with game_* tools.',
     S(id=STR, stop_at=INT, worktree=WT), ['id']),
    ('journal_list', t_journal_list, 'Recent match journals of a real install (desk = the user\'s Linux install, lab = the lab install). Read-only.',
     S(install=STR, limit=INT), []),
    ('journal_read', t_journal_read, 'Read a match journal: the event log ("events" lines: draws, plays, attacks, damage per turn), human actions, NPC decisions, '
     'choices, result. types filters record types (add "fx" for client spell events). Default: the newest match.',
     S(match=STR, install=STR, types={'type': 'array', 'items': STR}, grep=STR, turn_from=INT, turn_to=INT, limit=INT, offset=INT), []),
    ('live_connect', t_live_connect, 'Connect to the running game client\'s test-control endpoint (hook builds only). target: lab (Linux lab install, '
     'started with lab.sh play), desk (the user\'s own install, only if it is a hook build), vm (win11 over ssh).', S(target=STR, lane=INT, wait=INT), []),
    ('live_state', t_live_state, 'Client state summary: scene, match id, options/choice ids, both sides (hand and board as [id, card]) and '
     '"differences" between engine and client. raw=true returns the full reply.', S(raw=BOOL), []),
    ('live_play', t_live_play, 'Play one option in the live match by its source entity id (hand card, hero power, minion attack) with an optional '
     'target/position/sub, or end_turn=true; waits settle seconds and returns the client spell telemetry it caused (raw_fx keeps state churn).',
     S(entity=INT, target=INT, position=INT, sub=INT, end_turn=BOOL, settle=INT, raw_fx=BOOL), []),
    ('live_options', t_live_options, 'The client\'s current option packet (optionsId, options with targets and positions).', S(), []),
    ('live_act', t_live_act, 'Send one option through the client\'s own GameState.SendOption (index/target/sub/position from live_options).',
     S(index=INT, options_id=INT, target=INT, sub=INT, position=INT, wait=INT), ['index', 'options_id']),
    ('live_end_turn', t_live_end_turn, 'End the turn through the client.', S(options_id=INT, wait=INT), ['options_id']),
    ('live_choose', t_live_choose, 'Pick a Discover/choice entity (click=true clicks it in the UI instead).', S(choice_id=INT, entity_id=INT, click=BOOL, wait=INT), ['choice_id', 'entity_id']),
    ('live_wait_idle', t_live_wait_idle, 'Wait until the client finished animating after an action id.', S(after_action_id=STR, timeout_ms=INT), ['after_action_id']),
    ('live_entities', t_live_entities, 'Engine tags, wire projection and client actor state for entity ids.', S(entity_ids={'type': 'array', 'items': INT}), ['entity_ids']),
    ('live_fx', t_live_fx, 'Client spell/animation telemetry rows after a cursor, parsed; next is the cursor to pass on. raw=true keeps spell '
     'state churn.', S(after=INT, limit=INT, raw=BOOL), []),
    ('live_ui', t_live_ui, 'Menu UI elements with stable ids (to navigate menus with live_press): hit-testable ones, or all=true. raw=true '
     'returns the full reply.', S(all=BOOL, raw=BOOL), []),
    ('live_press', t_live_press, 'Press a UI element by id from live_ui.', S(element=STR, snapshot=STR, timeout_ms=INT), ['element']),
    ('live_screenshot', t_live_screenshot, 'Capture the game\'s own rendered frame (in-process Unity capture, not the desktop) and return it as an image.', S(), []),
    ('live_logs', t_live_logs, 'Tail of the client\'s Unity.log or wine.log, optionally filtered by regex.', S(which=STR, lines=INT, grep=STR), []),
    ('live_call', t_live_call, 'Raw test-control request {cmd, args}. Commands include state, options, choices, act, end-turn, mulligan-confirm, choose, '
     'choice-click, concede, entity, entity-drag, card-text, locate, ui, wait-ui, press, fx, fx-status, wait-until-idle, action-status, screenshot.',
     S(cmd=STR, args=OBJ, wait=INT), ['cmd']),
    ('live_disconnect', t_live_disconnect, 'Close the test-control connection.', S(), []),
]
TOOL_MAP = {name: fn for name, fn, *_ in TOOLS}


def tool_list():
    return [{'name': n, 'description': d, 'inputSchema': {'type': 'object', 'properties': p, 'required': r}} for n, _, d, p, r in TOOLS]


def call_tool(name, args):
    fn = TOOL_MAP.get(name)
    if not fn:
        raise ToolError('unknown tool ' + name)
    return fn(**(args or {}))


def content(result):
    if isinstance(result, dict) and '__image__' in result:
        img = result.pop('__image__')
        return [{'type': 'image', 'data': img, 'mimeType': 'image/png'}, {'type': 'text', 'text': json.dumps(result, indent=1)}]
    text = result if isinstance(result, str) else json.dumps(result, indent=1, ensure_ascii=False)
    if len(text) > 90000:
        text = text[:90000] + '\n... (truncated; narrow the request)'
    return [{'type': 'text', 'text': text}]


def handle(msg):
    method = msg.get('method')
    mid = msg.get('id')
    if method == 'initialize':
        return {'jsonrpc': '2.0', 'id': mid, 'result': {
            'protocolVersion': msg.get('params', {}).get('protocolVersion', '2025-06-18'),
            'capabilities': {'tools': {}},
            'serverInfo': {'name': 'solostone', 'version': '0.1.0'},
            'instructions': 'SoloStone test bench. Engine: scenario_run/game_*/fuzz_run against engine worktrees (default %s). '
                            'Live client: lab.sh builds and starts a hook build in a separate Linux install; then live_connect.' % DEFAULT_ENGINE}}
    if method == 'tools/list':
        return {'jsonrpc': '2.0', 'id': mid, 'result': {'tools': tool_list()}}
    if method == 'tools/call':
        p = msg.get('params', {})
        try:
            result = call_tool(p.get('name'), p.get('arguments'))
            return {'jsonrpc': '2.0', 'id': mid, 'result': {'content': content(result), 'isError': False}}
        except ToolError as e:
            return {'jsonrpc': '2.0', 'id': mid, 'result': {'content': [{'type': 'text', 'text': str(e)}], 'isError': True}}
        except TypeError as e:
            return {'jsonrpc': '2.0', 'id': mid, 'result': {'content': [{'type': 'text', 'text': 'bad arguments: %s' % e}], 'isError': True}}
        except Exception as e:
            log(traceback.format_exc())
            return {'jsonrpc': '2.0', 'id': mid, 'result': {'content': [{'type': 'text', 'text': '%s: %s' % (type(e).__name__, e)}], 'isError': True}}
    if method == 'ping':
        return {'jsonrpc': '2.0', 'id': mid, 'result': {}}
    if mid is None:
        return None  # notification
    return {'jsonrpc': '2.0', 'id': mid, 'error': {'code': -32601, 'message': 'method not found: %s' % method}}


def main():
    out_lock = threading.Lock()

    def respond(msg):
        reply = handle(msg)
        if reply is not None:
            with out_lock:
                sys.stdout.write(json.dumps(reply) + '\n')
                sys.stdout.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        # tools run one at a time per worker (worker lock); run calls in threads so ping and list stay responsive
        if msg.get('method') == 'tools/call':
            threading.Thread(target=respond, args=(msg,), daemon=True).start()
        else:
            respond(msg)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'call':
        # CLI for testing: solostone_mcp.py call <tool> '<json args>'
        r = call_tool(sys.argv[2], json.loads(sys.argv[3]) if len(sys.argv) > 3 else {})
        if isinstance(r, dict) and '__image__' in r:
            r.pop('__image__')
        print(json.dumps(r, indent=1))
    else:
        main()
