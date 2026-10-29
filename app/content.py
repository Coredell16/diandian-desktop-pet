"""Editable project data, validation and atomic saving; no GUI dependencies beyond Qt."""
import copy
import json
import math
import re
import wave
from pathlib import Path

from PyQt5.QtCore import QIODevice, QSaveFile
from PyQt5.QtGui import QImage


def asset_path(root, relative):
    root = Path(root).resolve()
    if not isinstance(relative, str) or not relative or Path(relative).is_absolute():
        raise ValueError('素材必须使用项目内的相对路径')
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or path == root:
        raise ValueError('素材路径不能超出项目文件夹')
    return path


def number(value, low, high, label):
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value) or not low <= value <= high:
        raise ValueError(f'{label}应在 {low} 到 {high} 之间')
    return value


def validate_project(data, root):
    data = copy.deepcopy(data)
    if not isinstance(data, dict) or data.get('schema_version') != 1:
        raise ValueError('不支持的项目格式')
    sounds, frames, actions = data.get('sounds'), data.get('frames'), data.get('actions')
    if not isinstance(sounds, list) or len(sounds) > 128 or not isinstance(frames, list) or len(frames) > 256:
        raise ValueError('叫声或动作素材数量不正确')
    if not isinstance(actions, dict) or not actions or len(actions) > 128:
        raise ValueError('项目至少需要一个动作，最多128个')
    sound_ids = set()
    for sound in sounds:
        if not isinstance(sound, dict) or not isinstance(sound.get('id'), str) or not re.fullmatch(r'[a-zA-Z0-9_-]+', sound['id']):
            raise ValueError('叫声编号只能使用英文、数字和下划线')
        if sound['id'] in sound_ids or sound['id'] in ('none', 'random'):
            raise ValueError('叫声编号重复或使用了保留名称')
        sound_ids.add(sound['id'])
        if not isinstance(sound.get('label'), str) or not sound['label'].strip() or len(sound['label']) > 60:
            raise ValueError('请为叫声填写名称（最多60字）')
        sound['enabled'] = bool(sound.get('enabled', True))
        path = asset_path(root, sound.get('file'))
        if not path.is_file() or path.stat().st_size > 30 * 1024**2:
            raise ValueError('叫声文件不存在或超过30MB：' + sound['label'])
        try:
            with wave.open(str(path), 'rb') as audio:
                if audio.getcomptype() != 'NONE' or audio.getsampwidth() != 2 or audio.getnchannels() not in (1, 2) or not 8000 <= audio.getframerate() <= 96000 or not 0 < audio.getnframes()/audio.getframerate() <= 120:
                    raise ValueError('叫声需为不超过120秒的16位PCM WAV，单声道或双声道')
        except (wave.Error, EOFError) as error:
            raise ValueError('无法读取WAV：' + sound['label']) from error
    images = {}
    for frame in frames:
        if not isinstance(frame, dict):
            raise ValueError('动作帧格式不正确')
        path = asset_path(root, frame.get('file'))
        if path not in images:
            if not path.is_file() or path.stat().st_size > 30*1024**2:
                raise ValueError('动作素材不存在或超过30MB')
            image = QImage(str(path))
            if image.isNull() or image.width() > 8192 or image.height() > 8192:
                raise ValueError('无法读取图片或图片超过8192像素')
            images[path] = (image.width(), image.height())
        rect, anchor = frame.get('rect'), frame.get('anchor')
        if not isinstance(rect, list) or len(rect) != 4 or not isinstance(anchor, list) or len(anchor) != 2:
            raise ValueError('动作帧需要裁剪矩形和定位点')
        for v in rect + anchor:
            number(v, 0, 8192, '帧坐标')
        x, y, w, h = rect
        iw, ih = images[path]
        if w <= 0 or h <= 0 or x+w > iw or y+h > ih or not x <= anchor[0] <= x+w or not y <= anchor[1] <= y+h:
            raise ValueError('裁剪或定位点超出图片范围')
        number(frame.get('scale'), .01, 10, '素材缩放')
        outline = frame.get('outline')
        if outline is not None:
            if not isinstance(outline, list) or not 3 <= len(outline) <= 32:
                raise ValueError('动作轮廓需要3至32个坐标点')
            for point in outline:
                if not isinstance(point, list) or len(point) != 2:
                    raise ValueError('动作轮廓坐标格式不正确')
                number(point[0], x, x+w, '轮廓横坐标')
                number(point[1], y, y+h, '轮廓纵坐标')
    if 'appearance' in data:
        from appearance import GROUPS, read_image, sheet_cells
        appearance = data['appearance']
        if not isinstance(appearance, dict) or set(appearance) != set(GROUPS) or len(frames) < 8:
            raise ValueError('个人外观需要完整的四组动作图和默认互动姿态。')
        for name in appearance.values():
            path = asset_path(root, name)
            if path.suffix.lower() != '.png':
                raise ValueError('个人外观动作图必须使用 PNG。')
            sheet_cells(read_image(path))
    for key, action in actions.items():
        if not isinstance(key, str) or not re.fullmatch(r'[a-zA-Z0-9_-]+', key) or key in ('idle','fly','land') or not isinstance(action, dict):
            raise ValueError('动作编号无效或使用了保留名称')
        if not isinstance(action.get('label'), str) or not action['label'].strip() or len(action['label']) > 60:
            raise ValueError('请为动作填写名称（最多60字）')
        action.setdefault('kind', 'frames')
        if action['kind'] not in ('frames','flight') or (action['kind'] == 'flight' and key != 'stretch'):
            raise ValueError('飞行帧保留给伸翅动作')
        seq = action.get('frames', [])
        if action['kind'] == 'frames' and (not isinstance(seq, list) or not 1 <= len(seq) <= 240 or any(type(i) is not int or not 0 <= i < 16+len(frames) for i in seq)):
            raise ValueError('动作帧编号不存在或序列为空：' + action['label'])
        number(action.get('fps'), 1, 30, '动作速度')
        number(action.get('duration'), .3, 120, '动作时长')
        number(action.get('auto_weight'), 0, 20, '自动活动权重')
        if action.get('sound') not in sound_ids | {'none','random'}:
            raise ValueError('动作引用了不存在的叫声：' + action['label'])
        if action.get('motion') not in ('none','pet','walk','bite','rock') or action.get('category') not in ('互动','动作'):
            raise ValueError('动作类型或分类不正确')
        if not isinstance(action.get('message'), str) or len(action['message']) > 100:
            raise ValueError('互动文字应不超过100字')
        action['enabled'], action['loop'] = bool(action.get('enabled', True)), bool(action.get('loop', False))
    interaction = data.get('interactions')
    if not isinstance(interaction, dict):
        raise ValueError('缺少互动设置')
    for trigger in ('click','double_click'):
        action = actions.get(interaction.get(trigger))
        if not action or not action['enabled']:
            raise ValueError('单击和双击必须选择已启用的动作')
    for low, high in [('idle_min','idle_max'),('call_min','call_max')]:
        number(interaction.get(low), 1, 3600, '活动间隔')
        number(interaction.get(high), 1, 3600, '活动间隔')
        if interaction[low] > interaction[high]:
            raise ValueError('最短间隔不能大于最长间隔')
    return data


def load_project(root):
    try:
        return validate_project(json.loads((Path(root)/'project.json').read_text(encoding='utf-8')), root)
    except (OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise ValueError('项目配置无法读取：' + str(error)) from error


def save_project(root, data):
    data = validate_project(data, root)
    target = QSaveFile(str(Path(root)/'project.json'))
    if not target.open(QIODevice.WriteOnly):
        raise OSError('无法保存项目：' + target.errorString())
    payload = (json.dumps(data, ensure_ascii=False, indent=2)+'\n').encode('utf-8')
    if target.write(payload) != len(payload) or not target.commit():
        raise OSError('项目保存失败：' + target.errorString())
    return data
