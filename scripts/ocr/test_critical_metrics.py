import unittest

import critical_metrics as metrics


class CriticalMetricChecks(unittest.TestCase):
    def test_price_substitution_and_decimal_punctuation(self):
        good = metrics.measure([dict(expected='Alice : S> 1,3m', actual='Alice : S> 1.3M')])
        self.assertEqual(1, good['exact_price_sequences'])
        bad = metrics.measure([dict(expected='Alice : S> 70k', actual='Alice : S> 10k')])
        self.assertEqual(0, bad['exact_price_sequences'])
        self.assertEqual(1, bad['digit_edit_errors'])

    def test_prices_ignore_digits_in_player_name(self):
        result = metrics.measure([dict(expected='31MHz : S> 335k', actual='3lMHz : S> 335K')])
        self.assertEqual(1, result['exact_price_sequences'])
        self.assertEqual(0, result['exact_names'])
        self.assertEqual(0, result['exact_digit_sequences'])

    def test_trade_marker_direction_and_invention(self):
        result = metrics.measure([
            dict(expected='Alice : B> Ore', actual='Alice : S> Ore'),
            dict(expected='Bob : Hello', actual='Bob : B> Hello'),
            dict(expected='Cara : s < Ore', actual='Cara : S< Ore')])
        self.assertEqual(2, result['tag_lines'])
        self.assertEqual(1, result['explicit_trade_tags'])
        self.assertEqual(1, result['invented_tag_lines'])

    def test_character_gain_does_not_override_numeric_gate(self):
        fields = dict(exact_names=20, exact_digit_sequences=18, exact_price_sequences=12, explicit_trade_tags=13)
        limits = dict(max_character_errors=21, min_exact_names=19, min_exact_digit_sequences=19,
                      min_exact_price_sequences=12, min_explicit_trade_tags=13)
        self.assertEqual(['exact_digit_sequences'], metrics.failures(fields, 10, limits))


if __name__ == '__main__':
    unittest.main()
