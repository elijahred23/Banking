const { test } = require('node:test');
const assert = require('node:assert/strict');
const { profiles, snapshot } = require('../src/Banking.Web/wwwroot/js/rail-comparison.js');

test('delivery and acceptance reserve principal without posting; settlement posts once', () => {
    for (const profile of profiles) {
        for (const amount of [1, 12500, 1000000000]) {
            for (let stage = 0; stage < 3; stage++) {
                const state = snapshot(profile, amount, stage);
                assert.equal(state.debit, 0);
                assert.equal(state.recipient, 0);
                assert.equal(state.held, amount);
                assert.equal(state.available + state.held, state.sender);
            }
            const state = snapshot(profile, amount, 3);
            assert.equal(state.held, 0);
            assert.equal(state.debit, amount);
            assert.equal(state.sender + state.recipient, 2000000000);
            assert.deepEqual(snapshot(profile, amount, 3), state);
        }
    }
});
test('return request does not restore money; consented return and declined return differ', () => {
    for (const profile of profiles) {
        assert.equal(snapshot(profile, 12500, 4).debit, 12500);
        const outcome = snapshot(profile, 12500, 5);
        assert.equal(outcome.debit, profile.returned ? 0 : 12500);
        assert.equal(outcome.sender + outcome.recipient, 2000000000);
        assert.equal(outcome.held, 0);
    }
});
test('FedNow ceiling is inclusive; ineligible copies never reserve or move funds', () => {
    const fedNow = profiles.find(x => x.name === 'FedNow');
    assert.equal(snapshot(fedNow, 1000000000, 3).blocked, false);
    for (let stage = 0; stage < 6; stage++) {
        const state = snapshot(fedNow, 1000000001, stage);
        assert.equal(state.blocked, true);
        assert.equal(state.held + state.debit + state.recipient, 0);
        assert.equal(state.available, 2000000000);
    }
});
