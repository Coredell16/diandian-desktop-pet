"""Diandian: a small, offline Windows desktop companion."""
import math
import random
import sys
import time
from pathlib import Path
import PyQt5

from PyQt5.QtCore import QCoreApplication, QPoint, QPointF, QRectF, QSettings, Qt, QTimer, QUrl
from PyQt5.QtGui import QColor, QFont, QIcon, QImage, QPainter, QPainterPath, QPixmap
from PyQt5.QtNetwork import QLocalServer, QLocalSocket
from PyQt5.QtMultimedia import QSoundEffect
from PyQt5.QtWidgets import QAction, QApplication, QMenu, QMessageBox, QSystemTrayIcon, QWidget

from content import load_project, save_project, validate_project, asset_path
from updater import VERSION, Updater, activate_install, launch_newer, persistent_root, wait_parent

PROGRAM_ROOT = Path(sys.executable).resolve().parent if getattr(sys, 'frozen', False) else Path(__file__).resolve().parent
ROOT = PROGRAM_ROOT / 'user-content' if (PROGRAM_ROOT / 'user-content/project.json').is_file() else PROGRAM_ROOT
if '--project-root' in sys.argv:
    ROOT = Path(sys.argv[sys.argv.index('--project-root') + 1]).resolve()


def load_sprite(name, root=ROOT):
    image = QImage(str(root / name)).convertToFormat(QImage.Format_RGBA8888)
    if image.isNull():
        raise RuntimeError('缺少素材：' + name)
    data = bytearray(image.constBits().asstring(image.byteCount()))
    for i in range(0, len(data), 4):
        if data[i + 3] < 5 or (data[i] > 225 and data[i + 2] < 75 and (data[i + 1] < 75 or data[i + 1] > 205)):
            data[i + 3] = 0
    return QPixmap.fromImage(QImage(bytes(data), image.width(), image.height(), image.bytesPerLine(), QImage.Format_RGBA8888).copy())


def load_frames(size, dpr, project=None, root=ROOT):
    """Render only the current display resolution; release large source atlases afterwards."""
    scale = size / 500

    def render(sheet, source, target, canvas=(500, 440), outline=None):
        frame = QPixmap(round(canvas[0] * scale * dpr), round(canvas[1] * scale * dpr))
        frame.setDevicePixelRatio(dpr)
        frame.fill(Qt.transparent)
        painter = QPainter(frame)
        painter.setRenderHint(QPainter.SmoothPixmapTransform)
        painter.scale(scale, scale)
        if outline:
            clip = QPainterPath()
            clip.moveTo(*outline[0])
            for point in outline[1:]:
                clip.lineTo(*point)
            clip.closeSubpath()
            painter.setClipPath(clip)
        painter.drawPixmap(QRectF(*target), sheet, QRectF(*source))
        painter.end()
        return frame

    sheet = load_sprite('motions.png', root)
    boxes = [(0,0,430,605),(380,0,410,605),(740,0,405,605),(1070,0,378,580),
             (0,550,460,536),(400,550,420,536),(740,550,440,536),(1080,580,368,506)]
    feet = [(149,484),(497,490),(855,492),(1232,489),
            (150,953),(533,965),(856,956),(1222,953)]
    paths = {
        0: [(0,0),(380,0),(380,480),(430,530),(430,605),(300,605),(250,550),(0,550)],
        1: [(380,0),(740,0),(740,480),(790,530),(790,605),(650,605),(600,550),(430,550),(430,530),(380,480)],
        2: [(740,0),(1070,0),(1070,450),(1145,530),(1145,605),(1000,605),(950,550),(790,550),(790,530),(740,480)],
        3: [(1070,0),(1448,0),(1448,580),(1145,580),(1145,530),(1070,450)],
        4: [(0,550),(250,550),(300,605),(400,605),(400,920),(460,980),(460,1086),(0,1086)],
        5: [(400,605),(450,605),(450,550),(600,550),(650,605),(740,605),(740,920),(820,990),(820,1086),(460,1086),(460,980),(400,920)],
        6: [(740,605),(800,605),(800,550),(950,550),(1000,605),(1080,605),(1080,920),(1180,1000),(1180,1086),(820,1086),(820,990),(740,920)],
        7: [(1080,605),(1150,605),(1150,580),(1448,580),(1448,1086),(1180,1086),(1180,1000),(1080,920)],
    }
    sx, sy = sheet.width() / 1448, sheet.height() / 1086
    normal = []
    for i, (fx, fy) in enumerate(feet):
        x, y, w, h = boxes[i]
        outline = [(190+(px-fx)*.74, 354+(py-fy)*.74) for px, py in paths[i]]
        normal.append(render(sheet, (x*sx,y*sy,w*sx,h*sy),
                             (190+(x-fx)*.74,354+(y-fy)*.74,w*.74,h*.74), outline=outline))

    sheet = load_sprite('curious.png', root)
    boxes = [(0,0,420,590),(360,0,400,590),(720,0,400,590),(1050,0,398,590),
             (0,590,480,496),(380,590,455,496),(720,590,480,496),(1100,540,348,546)]
    feet = [(135,493),(479,499),(828,501),(1192,495),
            (164,959),(541,966),(883,967),(1225,979)]
    paths = {
        0: [(0,0),(360,0),(360,450),(420,530),(420,590),(0,590)],
        1: [(360,0),(720,0),(720,440),(760,520),(760,590),(420,590),(420,530),(360,450)],
        2: [(720,0),(1050,0),(1050,450),(1120,540),(1120,590),(760,590),(760,520),(720,440)],
        3: [(1050,0),(1448,0),(1448,590),(1340,590),(1300,540),(1120,540),(1050,450)],
        4: [(0,590),(380,590),(380,900),(480,980),(480,1086),(0,1086)],
        5: [(380,590),(720,590),(720,900),(835,980),(835,1086),(480,1086),(480,980),(380,900)],
        6: [(720,590),(1100,590),(1100,920),(1200,1020),(1200,1086),(835,1086),(835,980),(720,900)],
        7: [(1100,615),(1150,615),(1150,540),(1300,540),(1340,590),(1448,590),(1448,1086),(1200,1086),(1200,1020),(1100,920)],
    }
    sx, sy = sheet.width() / 1448, sheet.height() / 1086
    for i, (fx, fy) in enumerate(feet):
        x, y, w, h = boxes[i]
        outline = [(190+(px-fx)*.74, 354+(py-fy)*.74) for px, py in paths[i]]
        normal.append(render(sheet, (x*sx,y*sy,w*sx,h*sy),
                             (190+(x-fx)*.74,354+(y-fy)*.74,w*.74,h*.74), outline=outline))

    # Two source poses end at the atlas edge. Reuse complete poses from this
    # approved set instead of showing a flat-cut tail or inventing more artwork.
    normal[3], normal[7] = normal[12], normal[5]

    extra_sheet, extra_name = None, None
    for item in (project or {}).get('frames', []):
        if item['file'] != extra_name:
            extra_name = item['file']
            extra_sheet = load_sprite(extra_name, root)
        x, y, w, h = item['rect']
        fx, fy = item['anchor']
        factor = item['scale']
        outline = [(210+(px-fx)*factor, 420+(py-fy)*factor)
                   for px, py in item.get('outline', [])]
        normal.append(render(extra_sheet, (x, y, w, h),
                             (210 - (fx-x)*factor, 420 - (fy-y)*factor, w*factor, h*factor), outline=outline))

    sheet = load_sprite('flight.png', root)
    boxes = [(0,0,490,477), (455,0,445,489), (900,0,435,489), (1335,0,439,489),
             (0,477,620,410), (530,489,480,398), (985,470,440,417), (1380,470,394,417)]
    eyes = [(113,200), (511,215), (963,217), (1424,216),
            (213,576), (677,575), (1038,578), (1477,575)]
    paths = {
        0: [(0,0),(455,0),(455,410),(490,450),(490,477),(0,477)],
        1: [(455,0),(900,0),(900,489),(490,489),(490,450),(455,410)],
        2: [(900,0),(1335,0),(1335,489),(1200,489),(1150,465),(900,465)],
        3: [(1335,0),(1774,0),(1774,489),(1650,489),(1600,465),(1335,465)],
        4: [(0,477),(620,477),(620,620),(570,670),(530,720),(575,887),(0,887)],
        5: [(620,489),(985,489),(985,800),(1010,840),(1010,887),(575,887),(530,720),(570,670),(620,620)],
        6: [(985,470),(1150,470),(1200,489),(1380,489),(1380,800),(1425,850),(1425,887),(1010,887),(1010,840),(985,800)],
        7: [(1380,470),(1600,470),(1650,489),(1774,489),(1774,887),(1425,887),(1425,850),(1380,800)],
    }
    sx, sy = sheet.width() / 1774, sheet.height() / 887
    flight = []
    for i, (eye_x, eye_y) in enumerate(eyes):
        x, y, w, h = boxes[i]
        outline = [(260+px-eye_x, 210+py-eye_y) for px, py in paths[i]]
        flight.append(render(sheet, (x * sx, y * sy, w * sx, h * sy),
                             (260+x-eye_x, 210+y-eye_y, w, h), (700,640), outline))
    if (project or {}).get('appearance'):
        from appearance import GROUPS, read_image, sheet_cells
        for group, start in zip(GROUPS, (0, 8, 16, 0)):
            cells = sheet_cells(read_image(asset_path(root, project['appearance'][group])))
            flying = group == 'flight'
            target = flight if flying else normal
            canvas = (700, 640) if flying else (500, 440)
            center, bottom = (350, 610) if flying else (210, 420)
            limit_w, limit_h = (620, 580) if flying else (380, 390)
            for i, cell in enumerate(cells):
                factor = min(limit_w / cell.width(), limit_h / cell.height())
                w, h = cell.width() * factor, cell.height() * factor
                target[start + i] = render(QPixmap.fromImage(cell),
                    (0, 0, cell.width(), cell.height()), (center - w / 2, bottom - h, w, h), canvas)
    return normal, flight



def action_pair(item, elapsed):
    seq = item['frames']
    phase = max(0, elapsed) * item['fps']
    step = int(phase)
    blend = phase-step
    blend = blend*blend*(3-2*blend)
    if item['loop']:
        first, second = seq[step % len(seq)], seq[(step+1) % len(seq)]
    else:
        first, second = seq[min(step,len(seq)-1)], seq[min(step+1,len(seq)-1)]
    # Raised and resting crests are mutually exclusive poses, never transparent layers.
    if first >= 8 or second >= 8 or 3 in (first, second):
        pose = first if blend < .5 else second
        return pose, pose, 0.0
    return first, second, blend


def pose_sprite(sprites, first, second, weight, cache):
    if first == second:
        return sprites[first]
    key = (sprites[first].cacheKey(), sprites[second].cacheKey())
    if key not in cache:
        frames = []
        for step in range(7):
            blend = step / 6
            pixmap = QPixmap(sprites[first].size())
            pixmap.setDevicePixelRatio(sprites[first].devicePixelRatio())
            pixmap.fill(Qt.transparent)
            painter = QPainter(pixmap)
            painter.setOpacity(1 - blend)
            painter.drawPixmap(0, 0, sprites[first])
            painter.setCompositionMode(QPainter.CompositionMode_Plus)
            painter.setOpacity(blend)
            painter.drawPixmap(0, 0, sprites[second])
            painter.end()
            frames.append(pixmap)
        if len(cache) >= 8:
            cache.pop(next(iter(cache)))
        cache[key] = frames
    return cache[key][round(weight * 6)]


def apply_motion(painter, motion, elapsed, size):
    factor = size/150
    if motion == 'bite':
        # Reach once, close the beak, and settle; crest poses come from the atlas.
        reach = max(0, math.sin(min(1, max(0, elapsed)/1.3)*math.pi))
        painter.translate(-8*factor*reach, 2*factor*reach)
    elif motion == 'rock':
        painter.translate(size*.42,size*.84)
        painter.rotate(math.sin(elapsed*math.tau*.75)*5)
        painter.translate(-size*.42,-size*.84)
    elif motion == 'pet':
        painter.translate(0,-abs(math.sin(elapsed*3))*2*factor)
    elif motion == 'walk':
        painter.translate(0,-abs(math.sin(elapsed*11))*1.5*factor)


class Diandian(QWidget):
    def __init__(self, settings=None, content_root=None):
        super().__init__()
        self.settings = settings if settings is not None else QSettings(QSettings.IniFormat, QSettings.UserScope, 'Diandian', 'DesktopPet')
        self.content_root = Path(content_root or ROOT)
        self.project = load_project(self.content_root)
        self.editor = None
        self.preview_blends = {}
        self.restoring = True
        self.setWindowTitle('点点 · 珍珠玄凤桌宠 v' + VERSION)
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)
        self.setMouseTracking(True)
        self.setCursor(Qt.OpenHandCursor)
        self.flight_index = 0
        self.flight_steps = []
        self.flight_next_at = 0.0
        self.size_px = self.settings.value('size', 150, type=int)
        if self.size_px not in (120, 150, 210):
            self.size_px = 150
        self.sleeping = False
        self.mode = 'idle'
        self.started = time.monotonic()
        self.direction = -1
        self.walk_remainder = 0.0
        self.last_tick = time.monotonic()
        self.next_action = time.monotonic() + random.uniform(6, 12)
        self.lively = self.settings.value('lively', True, type=bool)
        self.until = 0.0
        self.message = ''
        self.bubble_until = 0.0
        self.blink_at = time.monotonic() + random.uniform(3, 6)
        self.blink_until = 0.0
        self.drag_origin = None
        self.drag_target = None
        self.dragged = False
        self.menu_open = False
        self.hovered = False
        self.muted = self.settings.value('muted', False, type=bool)
        self.volume = max(0.0, min(1.0, self.settings.value('volume', 0.30, type=float)))
        self.auto_calls = self.settings.value('auto_calls', False, type=bool)
        self.next_call = time.monotonic() + random.uniform(self.project['interactions']['call_min'], self.project['interactions']['call_max'])
        self.calls = [c for c in self.project['sounds'] if c['enabled']]
        self.call_bag = []
        self.last_call = None
        self.pending_call = False
        self.sound = QSoundEffect(self)
        self.sound.setVolume(self.volume)
        self.sound.statusChanged.connect(self.sound_ready)
        self.resize_pet(self.size_px)
        self.home()
        position = self.settings.value('position', None)
        if isinstance(position, QPoint):
            self.move(position)
            self.keep_on_screen()
        self.roam_center = self.x()
        self.setToolTip('点点 v' + VERSION + '：单击摸摸 · 拖动展翅 · 双击伸翅 · 右键菜单')
        self.tray = QSystemTrayIcon(QIcon(str(self.content_root / 'diandian.ico')), self)
        self.updater = Updater(self, PROGRAM_ROOT)
        self.tray.setToolTip('点点 v' + VERSION + ' · 写实二维桌宠')
        self.tray.activated.connect(self.tray_clicked)
        self.refresh_tray_menu()
        self.tray.show()
        self.click_timer = QTimer(self)
        self.click_timer.setSingleShot(True)
        self.click_timer.timeout.connect(lambda: self.play_action(self.project['interactions']['click']))
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.setTimerType(Qt.PreciseTimer)
        self.timer.start(16)
        self.restoring = False

    def resize_pet(self, size):
        self.size_px = size
        self.setFixedSize(size + 72, size + 76)
        dpr = self.devicePixelRatioF()
        self.scaled, self.flight_scaled = load_frames(size, dpr, self.project, self.content_root)
        self.blends = {}
        if self.isVisible():
            self.keep_on_screen()
        if hasattr(self, 'tray'):
            self.refresh_tray_menu()
        self.save_settings()
        self.update()

    def save_settings(self):
        if self.restoring:
            return
        for key, value in [('size', self.size_px), ('volume', self.volume), ('muted', self.muted),
                           ('auto_calls', self.auto_calls), ('lively', self.lively), ('position', self.pos())]:
            self.settings.setValue(key, value)
        self.settings.sync()

    def cancel_click(self):
        if hasattr(self, 'click_timer'):
            self.click_timer.stop()

    def home(self):
        area = QApplication.primaryScreen().availableGeometry()
        self.move(area.right() - self.width() - 12, area.bottom() - self.height() - 5)
        self.roam_center = self.x()
        self.save_settings()

    def keep_on_screen(self):
        screen = QApplication.screenAt(self.frameGeometry().center()) or QApplication.primaryScreen()
        area = screen.availableGeometry()
        self.move(max(area.left(), min(self.x(), area.right() - self.width() + 1)),
                  max(area.top(), min(self.y(), area.bottom() - self.height() + 1)))

    def say(self, text, seconds=2.8):
        self.message = text
        self.bubble_until = time.monotonic() + seconds
        self.update()

    def interact(self, action):
        self.play_action(action)

    def play_action(self, action):
        item = self.project['actions'].get(action)
        if not item or not item['enabled']:
            return
        self.sleeping = False
        self.start_action(action)
        if item['message']:
            self.say(item['message'])
        sound = item['sound']
        if sound == 'random':
            self.call()
        elif sound != 'none':
            index = next((i for i,c in enumerate(self.calls) if c['id'] == sound), None)
            if index is not None:
                self.call(index, force=True)
        self.refresh_tray_menu()

    def apply_project(self, project):
        project = validate_project(project, self.content_root)
        normal, flight = load_frames(self.size_px, self.devicePixelRatioF(), project, self.content_root)
        save_project(self.content_root, project)
        self.stop_call()
        self.cancel_click()
        self.project = project
        self.scaled, self.flight_scaled, self.blends = normal, flight, {}
        self.calls = [c for c in project['sounds'] if c['enabled']]
        self.call_bag, self.last_call = [], None
        self.mode = 'idle'
        self.next_action = time.monotonic() + project['interactions']['idle_min']
        self.next_call = time.monotonic() + project['interactions']['call_min']
        self.refresh_tray_menu()
        self.update()

    def open_editor(self):
        self.cancel_click()
        if self.editor is None:
            from editor import ProjectEditor
            self.editor = ProjectEditor(self)
            self.editor.destroyed.connect(self.editor_closed)
        self.editor.show()
        self.editor.raise_()
        self.editor.activateWindow()

    def editor_closed(self):
        self.editor = None
        self.preview_blends.clear()

    def preview_frames(self, project, size=280):
        self.preview_blends.clear()
        return load_frames(size, 1, project, self.content_root)

    def draw_preview(self, painter, bounds, project, action_id, elapsed, normal, flight):
        item = project['actions'].get(action_id)
        if not item:
            return
        if item['kind'] != 'flight' and (not item['frames'] or any(i < 0 or i >= len(normal) for i in item['frames'])):
            return
        size = normal[0].width() / normal[0].devicePixelRatio()
        painter.save()
        painter.setRenderHint(QPainter.SmoothPixmapTransform)
        factor = min(bounds.width()/(size*1.4), bounds.height()/(size*1.3))
        painter.translate(bounds.center().x()-size*factor/2, bounds.center().y()-size*.44*factor)
        painter.scale(factor, factor)
        if item['kind'] == 'flight':
            seq = [0,1,2,3,4,4,4,3,2,1,0]
            index = seq[min(int(elapsed*6),len(seq)-1)]
            painter.drawPixmap(QPointF(-size*.2,-size*.2),flight[index])
        else:
            first, second, weight = action_pair(item, elapsed)
            apply_motion(painter, item['motion'], elapsed, size)
            painter.drawPixmap(0, 0, pose_sprite(normal, first, second, weight, self.preview_blends))
        painter.restore()

    def start_action(self, action):
        self.cancel_click()
        now = time.monotonic()
        in_flight = self.mode in ('fly', 'land', 'stretch')
        if in_flight:
            self.advance_flight(now)
            in_flight = self.mode in ('fly', 'land', 'stretch')
        if action in ('fly', 'land', 'stretch'):
            if not in_flight:
                self.flight_index = 0
                self.flight_next_at = now + 1 / 18
            current = self.flight_index
            closing = list(range(current + 1, 8)) + [1, 0] if current >= 2 else ([0] if current else [])
            if action == 'fly':
                self.flight_steps = list(range(current + 1, 3)) if current < 2 else []
            elif action == 'land':
                self.flight_steps = closing
            else:
                self.flight_steps = closing + [1, 2, 3] + [4] * 18 + [3, 2, 1, 0]
        self.mode = action
        self.started = now
        self.until = float('inf') if action in ('fly', 'land', 'stretch') else now + self.project['actions'].get(action, {}).get('duration', 2)
        if self.project['actions'].get(action, {}).get('motion') == 'walk':
            self.direction = 1 if self.x() < self.roam_center - 30 else -1
        if hasattr(self, 'timer'):
            self.timer.setInterval(16)
        self.update()

    def advance_flight(self, now):
        while self.mode in ('fly', 'land', 'stretch') and now >= self.flight_next_at:
            if self.flight_steps:
                self.flight_index = self.flight_steps.pop(0)
            elif self.mode == 'fly':
                steps = int((now - self.flight_next_at) * 18) + 1
                self.flight_index = 2 + (self.flight_index - 2 + steps) % 6
                self.flight_next_at += steps / 18
                break
            else:
                self.mode = 'idle'
                break
            self.flight_next_at += 1 / 18

    def demonstrate(self, action):
        self.sleeping = False
        self.start_action(action)

    def toggle_lively(self):
        self.cancel_click()
        self.lively = not self.lively
        self.mode = 'idle'
        self.next_action = time.monotonic() + 6
        self.refresh_tray_menu()
        self.save_settings()

    def frame_pair(self, now):
        if self.sleeping:
            return 1, 1, 0.0
        elapsed = now - self.started
        if self.mode in self.project['actions'] and self.project['actions'][self.mode]['kind'] == 'frames':
            return action_pair(self.project['actions'][self.mode], elapsed)
        frame = 1 if now < self.blink_until else 0
        return frame, frame, 0.0

    def frame_index(self, now):
        return self.frame_pair(now)[0]

    def animated_sprite(self, now):
        if self.mode in ('fly', 'land', 'stretch'):
            self.advance_flight(now)
            if len(self.flight_scaled) != 8:
                raise RuntimeError('飞行动作素材尚未完整安装')
            return self.flight_scaled[self.flight_index]
        first, second, weight = self.frame_pair(now)
        return pose_sprite(self.scaled, first, second, weight, self.blends)

    def draw_bird(self, painter, now):
        flying = self.mode in ('fly', 'land', 'stretch')
        sprite = self.animated_sprite(now)
        offset = -self.size_px * 0.2 if flying else 0
        painter.save()
        if self.direction == 1:
            painter.translate(self.size_px, 0)
            painter.scale(-1, 1)
        if not flying:
            apply_motion(painter, self.project['actions'].get(self.mode, {}).get('motion', 'none'), now-self.started, self.size_px)
        painter.drawPixmap(QPointF(offset, offset), sprite)
        painter.restore()

    def next_call_index(self):
        if not self.call_bag:
            self.call_bag = list(range(len(self.calls)))
            random.shuffle(self.call_bag)
            if len(self.call_bag) > 1 and self.call_bag[-1] == self.last_call:
                self.call_bag[-1], self.call_bag[0] = self.call_bag[0], self.call_bag[-1]
        index = self.call_bag.pop()
        self.last_call = index
        return index

    def sound_ready(self):
        if not self.pending_call:
            return
        if self.muted or self.sleeping:
            self.pending_call = False
        elif self.sound.status() == QSoundEffect.Ready:
            self.pending_call = False
            self.sound.play()
        elif self.sound.status() == QSoundEffect.Error:
            self.pending_call = False
            self.say('这段叫声暂时无法播放')

    def stop_call(self):
        self.pending_call = False
        self.sound.stop()

    def call(self, index=None, force=False):
        if force:
            self.cancel_click()
        if self.muted or self.sleeping or not self.calls:
            return
        if not force and (self.sound.isPlaying() or self.pending_call):
            return
        self.stop_call()
        if index is None:
            index = self.next_call_index()
        else:
            self.last_call = index
            self.call_bag = []
        url = QUrl.fromLocalFile(str(asset_path(self.content_root, self.calls[index]['file'])))
        if self.sound.source() != url:
            self.sound.setSource(url)
        self.pending_call = True
        self.sound_ready()  # Also handles already-cached sources synchronously.

    def set_volume(self, volume):
        self.cancel_click()
        self.volume = volume
        self.sound.setVolume(volume)
        self.refresh_tray_menu()
        self.save_settings()

    def toggle_mute(self):
        self.cancel_click()
        self.muted = not self.muted
        if self.muted:
            self.stop_call()
        self.say('安静陪着你' if self.muted else '叫声已开启')
        self.refresh_tray_menu()
        self.save_settings()

    def toggle_auto_calls(self):
        self.cancel_click()
        self.auto_calls = not self.auto_calls
        self.next_call = time.monotonic() + random.uniform(self.project['interactions']['call_min'], self.project['interactions']['call_max'])
        self.refresh_tray_menu()
        self.save_settings()

    def toggle_sleep(self):
        self.cancel_click()
        self.sleeping = not self.sleeping
        self.stop_call()
        self.mode = 'idle'
        self.say('晚安，陪你歇一会儿' if self.sleeping else '醒啦！啾～')
        self.refresh_tray_menu()

    def tick(self):
        now = time.monotonic()
        self.advance_flight(now)
        delta = min(now - self.last_tick, 0.1)
        self.last_tick = now
        if now >= self.blink_at:
            self.blink_until = now + 0.18
            self.blink_at = now + random.uniform(3.0, 7.0)
        if now >= self.until and self.mode not in ('fly', 'land', 'stretch'):
            self.mode = 'idle'
        if self.dragged and self.drag_target is not None and self.drag_origin is not None:
            if self.pos() != self.drag_target:
                self.move(self.drag_target)
        if self.project['actions'].get(self.mode, {}).get('motion') == 'walk' and self.drag_origin is None and not self.menu_open:
            self.walk_remainder += delta * self.direction * 18
            step = int(self.walk_remainder)
            self.walk_remainder -= step
            self.move(self.x() + step, self.y())
            self.keep_on_screen()
        if (now >= self.next_action and self.mode == 'idle' and not self.sleeping
                and self.drag_origin is None and not self.menu_open and not self.click_timer.isActive() and self.editor is None):
            candidates = [(key,a['auto_weight']) for key,a in self.project['actions'].items() if a['enabled'] and a['auto_weight'] > 0]
            if self.lively and candidates:
                self.play_action(random.choices([x[0] for x in candidates], weights=[x[1] for x in candidates])[0])
            self.next_action = now + random.uniform(self.project['interactions']['idle_min'], self.project['interactions']['idle_max'])
        if now >= self.next_call:
            if self.auto_calls and not self.menu_open and self.drag_origin is None:
                self.call()
            self.next_call = now + random.uniform(self.project['interactions']['call_min'], self.project['interactions']['call_max'])
        interval = 250 if self.sleeping else (50 if self.mode == 'idle' else 16)
        if self.timer.interval() != interval:
            self.timer.setInterval(interval)
        self.update()

    def paintEvent(self, event):
        now = time.monotonic()
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.SmoothPixmapTransform)
        angle = 0.0
        hop = 0.0
        if self.mode == 'fly':
            hop = math.sin((now-self.started)*math.tau*2)*0.6
        elif self.hovered and not self.sleeping and self.mode == 'idle':
            angle = -2.0
        breath = math.sin(now * (1.8 if self.sleeping else 2.4)) * 0.7
        p.save()
        p.translate(36 + self.size_px * 0.42 + self.walk_remainder, 65 + self.size_px * 0.95 + hop)
        p.rotate(angle)
        p.scale(1, 1 + breath / self.size_px)
        p.translate(-self.size_px * 0.42, -self.size_px * 0.95)
        self.draw_bird(p, now)
        p.restore()
        p.setFont(QFont('Microsoft YaHei', 10))
        if now < self.bubble_until:
            width = min(self.width() - 16, p.fontMetrics().horizontalAdvance(self.message) + 28)
            rect = QRectF((self.width() - width) / 2, 12, width, 34)
            p.setPen(QColor('#d9d6cd'))
            p.setBrush(QColor(255, 253, 246, 242))
            p.drawRoundedRect(rect, 13, 13)
            p.setPen(QColor('#514a3e'))
            p.drawText(rect, Qt.AlignCenter, self.message)
        elif self.sleeping:
            p.setPen(QColor('#78869a'))
            p.drawText(self.width() // 2, 48, 'z Z')
        if self.mode == 'eat':
            p.setPen(Qt.NoPen)
            p.setBrush(QColor('#d7ab55'))
            for i in range(6):
                p.drawEllipse(QRectF(35 + i * 5, self.height() - 19 + (i % 2) * 3, 4, 3))
        p.end()

    def refresh_tray_menu(self):
        previous = self.tray.contextMenu()
        self.tray.setContextMenu(self.menu())
        if previous:
            previous.deleteLater()

    def menu(self):
        menu = QMenu(self)
        menu.setFont(QFont('Microsoft YaHei', 10))
        menu.aboutToShow.connect(lambda: setattr(self, 'menu_open', True))
        menu.aboutToShow.connect(self.cancel_click)
        menu.aboutToHide.connect(lambda: setattr(self, 'menu_open', False))
        menu.setStyleSheet('QMenu { background: #fffdf6; color: #514a3e; padding: 6px; }'
                           'QMenu::item { padding: 7px 20px; }'
                           'QMenu::item:selected { background: #eee5ce; border-radius: 5px; }'
                           'QMenu::separator { height: 1px; background: #e7e0d0; margin: 5px 9px; }')
        title = menu.addAction('点点 · v' + VERSION)
        title.setEnabled(False)
        menu.addSeparator()
        interaction = menu.addMenu('互动')
        motions = menu.addMenu('动作')
        for key, item in self.project['actions'].items():
            if item['enabled']:
                target = interaction if item['category'] == '互动' else motions
                target.addAction(item['label'], lambda checked=False, k=key: self.play_action(k))
        interaction.addAction('叫一声', lambda: self.call(force=True))
        motions.addSeparator()
        lively_action = motions.addAction('自己活动', self.toggle_lively)
        lively_action.setCheckable(True)
        lively_action.setChecked(self.lively)
        sounds_menu = menu.addMenu('声音')
        mute_action = sounds_menu.addAction('静音', self.toggle_mute)
        mute_action.setCheckable(True)
        mute_action.setChecked(self.muted)
        auto_action = sounds_menu.addAction('偶尔自己叫一声', self.toggle_auto_calls)
        auto_action.setCheckable(True)
        auto_action.setChecked(self.auto_calls)
        sounds_menu.addSeparator()
        for label, volume in [('轻声 15%', 0.15), ('适中 30%', 0.30), ('响亮 60%', 0.60)]:
            action = sounds_menu.addAction(label, lambda checked=False, v=volume: self.set_volume(v))
            action.setCheckable(True)
            action.setChecked(volume == self.volume)
        clips = sounds_menu.addMenu('叫声')
        for i, clip in enumerate(self.calls):
            clips.addAction(clip['label'], lambda checked=False, n=i: self.call(n, force=True))
        size_menu = menu.addMenu('大小与位置')
        for label, size in [('迷你', 120), ('小巧（默认）', 150), ('适中', 210)]:
            action = QAction(label, size_menu)
            action.setCheckable(True)
            action.setChecked(size == self.size_px)
            action.triggered.connect(lambda checked, s=size: self.resize_pet(s))
            size_menu.addAction(action)
        size_menu.addSeparator()
        size_menu.addAction('回到右下角', self.home)
        menu.addSeparator()
        menu.addAction('叫醒点点' if self.sleeping else '休息一会儿', self.toggle_sleep)
        menu.addSeparator()
        menu.addAction('编辑点点…', self.open_editor)
        menu.addAction('更新 v1…', self.updater.open)
        menu.addAction('退出点点', QApplication.instance().quit)
        return menu

    def contextMenuEvent(self, event):
        self.cancel_click()
        menu = self.menu()
        menu.exec_(event.globalPos())
        menu.deleteLater()

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self.click_timer.stop()
            self.drag_origin = event.globalPos()
            if self.mode not in ('fly', 'land', 'stretch'):
                self.mode = 'idle'
            self.window_origin = self.pos()
            self.dragged = False
            self.setCursor(Qt.ClosedHandCursor)

    def mouseMoveEvent(self, event):
        if self.drag_origin is not None and event.buttons() & Qt.LeftButton:
            delta = event.globalPos() - self.drag_origin
            if delta.manhattanLength() > QApplication.startDragDistance():
                if not self.dragged:
                    self.sleeping = False
                    self.start_action('fly')
                self.dragged = True
            if self.dragged:
                self.drag_target = self.window_origin + delta

    def mouseReleaseEvent(self, event):
        if event.button() == Qt.LeftButton and self.drag_origin is not None:
            if not self.dragged:
                self.click_timer.start(QApplication.doubleClickInterval())
            else:
                if self.drag_target is not None:
                    self.move(self.drag_target)
                self.start_action('land')
            self.drag_origin = None
            self.drag_target = None
            self.setCursor(Qt.OpenHandCursor)
            self.keep_on_screen()
            self.roam_center = self.x()
            self.save_settings()

    def mouseDoubleClickEvent(self, event):
        if event.button() == Qt.LeftButton:
            self.click_timer.stop()
            self.drag_origin = None
            self.drag_target = None
            self.setCursor(Qt.OpenHandCursor)
            self.play_action(self.project['interactions']['double_click'])

    def enterEvent(self, event):
        self.hovered = True

    def leaveEvent(self, event):
        self.hovered = False

    def tray_clicked(self, reason):
        if reason in (QSystemTrayIcon.Trigger, QSystemTrayIcon.DoubleClick):
            self.show()
            self.raise_()
            self.say('我在这里～')

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Escape:
            QApplication.instance().quit()


def self_test(app):
    from checks import run
    run(app)


if __name__ == '__main__':
    # Qt 5.15.2 can misread Chinese paths in its built-in plugin prefix.
    # Supply the actual installed/bundled directory before creating the GUI.
    plugins = Path(PyQt5.__file__).resolve().parent / 'Qt5' / 'plugins'
    if plugins.is_dir():
        QCoreApplication.addLibraryPath(str(plugins))
    QApplication.setAttribute(Qt.AA_EnableHighDpiScaling)
    QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps)
    app = QApplication(sys.argv)
    app.setWindowIcon(QIcon(str(ROOT / 'diandian.ico')))
    app.setQuitOnLastWindowClosed(False)
    if '--self-test' in sys.argv:
        try:
            self_test(app)
        except Exception:
            import traceback
            report = Path(sys.argv[2]) if len(sys.argv) > 2 else Path.cwd() / 'work'
            report.mkdir(parents=True, exist_ok=True)
            details = traceback.format_exc()
            (report / 'self-test-error.txt').write_text(details, encoding='utf-8')
            if sys.stderr:
                sys.stderr.write(details)
            sys.exit(1)
        sys.exit(0)
    try:
        if '--wait-parent' in sys.argv:
            wait_parent(int(sys.argv[sys.argv.index('--wait-parent') + 1]))
        forwarded = ['--editor'] if '--editor' in sys.argv else []
        if '--project-root' in sys.argv:
            forwarded += ['--project-root', str(ROOT)]
        if getattr(sys, 'frozen', False) and launch_newer(PROGRAM_ROOT, persistent_root(), forwarded):
            sys.exit(0)
    except (ValueError, IndexError) as error:
        QMessageBox.critical(None, '点点更新交接未完成', str(error))
        sys.exit(1)
    socket = QLocalSocket()
    socket.connectToServer('diandian-pearl-cockatiel-local-v1')
    if socket.waitForConnected(200):
        if '--editor' in sys.argv:
            socket.write(b'editor')
            socket.waitForBytesWritten(500)
        socket.disconnectFromServer()
        sys.exit(0)
    server = QLocalServer()
    if not server.listen('diandian-pearl-cockatiel-local-v1'):
        raise RuntimeError('点点已经在运行，或本地启动通道不可用。')
    try:
        bird = Diandian()
    except (ValueError, OSError) as error:
        QMessageBox.critical(None, '点点项目无法打开', str(error))
        sys.exit(1)
    app.aboutToQuit.connect(bird.save_settings)
    app.aboutToQuit.connect(bird.updater.stop)
    if getattr(sys, 'frozen', False):
        try:
            activate_install(PROGRAM_ROOT, persistent_root())
        except (OSError, ValueError):
            bird.say('新版已启动，但更新入口未保存。')
    bird.updater.start()

    def reveal():
        connection = server.nextPendingConnection()
        if connection:
            if not connection.bytesAvailable():
                connection.waitForReadyRead(300)
            command = bytes(connection.readAll())
            if command == b'editor':
                bird.open_editor()
            connection.close()
            connection.deleteLater()
        bird.show()
        bird.raise_()
        bird.say('点点在这里～')

    server.newConnection.connect(reveal)
    bird.show()
    bird.say('你好，我是点点')
    if '--editor' in sys.argv:
        bird.open_editor()
    sys.exit(app.exec_())
