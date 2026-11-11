"""Field-level regression checks for reviewed MapleStory OCR lines."""
import re

from dataset import distance


def sender(text):
    return text.split(':', 1)[0].strip() if ':' in text else None


def price_tokens(text):
    body = text.split(':', 1)[-1]
    return [(number.replace(',', '.'), unit.lower()) for number, unit in
            re.findall(r'(\d+(?:[.,]\d+)?)\s*([km])', body, flags=re.I)]


def trade_tags(text):
    return re.findall(r'(?<!\w)([bs])\s*([><])', text.lower())


def measure(details):
    result = dict(lines=0, exact_names=0, exact_digit_sequences=0, digit_edit_errors=0,
                  exact_price_sequences=0, price_lines=0, explicit_trade_tags=0, tag_lines=0,
                  invented_tag_lines=0)
    for row in details:
        expected, actual = row['expected'], row['actual']
        result['lines'] += 1
        result['exact_names'] += sender(expected) is not None and sender(expected) == sender(actual)
        expected_digits = re.sub(r'[^0-9]', '', expected)
        actual_digits = re.sub(r'[^0-9]', '', actual)
        result['exact_digit_sequences'] += expected_digits == actual_digits
        result['digit_edit_errors'] += distance(expected_digits, actual_digits)
        expected_prices, actual_prices = price_tokens(expected), price_tokens(actual)
        if expected_prices:
            result['price_lines'] += 1
            result['exact_price_sequences'] += expected_prices == actual_prices
        expected_tags, actual_tags = trade_tags(expected), trade_tags(actual)
        if expected_tags:
            result['tag_lines'] += 1
            result['explicit_trade_tags'] += expected_tags == actual_tags
        elif actual_tags:
            result['invented_tag_lines'] += 1
    return result


def failures(fields, character_errors, limits):
    failed = []
    if character_errors > limits['max_character_errors']:
        failed.append('character_errors')
    for key in ['exact_names', 'exact_digit_sequences', 'exact_price_sequences', 'explicit_trade_tags']:
        if fields[key] < limits['min_' + key]:
            failed.append(key)
    return failed
