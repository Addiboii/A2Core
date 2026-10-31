import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import quest_reference as qr  # noqa: E402


def test_reference_filters_cap_and_flags_missing_types():
    data = {'quests': {
        'A': {'id': 1, 'level': 10, 'steps': [{'order': 1, 'objectives': [{'type': 'KillNpc'}]}]},
        'B': {'id': 2, 'level': 10, 'steps': [{'order': 1, 'objectives': [{'type': 'ClearQuestExplorationType'}]}]},
        'C': {'id': 3, 'level': 50, 'steps': []},
        'D': {'id': 4, 'level': 1, 'unavailable': True, 'steps': []},
    }}
    ru = {'A': {'title': 'A_ru', 'steps': [{'order': 1, 'objectives': [{'text': 'kill_ru'}]}]}}
    quests = qr.reference(data, 45, {'KillNpc'}, ru)
    assert [q['code'] for q in quests] == ['A', 'B']
    assert quests[0]['supported'] and quests[0]['title_ru'] == 'A_ru'
    assert quests[0]['steps'][0]['objectives'][0]['text_ru'] == 'kill_ru'
    assert not quests[1]['supported']
    assert '**missing**' in qr.coverage_md(quests, {'KillNpc'}, 45)
