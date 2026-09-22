use serde::Serialize;
use std::cmp::{max, min};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Rational {
    n: i128,
    d: i128,
}
impl Ord for Rational {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        (self.n * other.d).cmp(&(other.n * self.d))
    }
}
impl PartialOrd for Rational {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Rational {
    pub fn new(mut n: i128, mut d: i128) -> Self {
        assert_ne!(d, 0);
        if d < 0 {
            n = -n;
            d = -d;
        }
        let g = gcd(n.unsigned_abs(), d as u128) as i128;
        Self { n: n / g, d: d / g }
    }
    pub fn integer(n: i128) -> Self {
        Self::new(n, 1)
    }
    pub fn text(self) -> String {
        if self.d == 1 {
            self.n.to_string()
        } else {
            format!("{}/{}", self.n, self.d)
        }
    }
}
fn gcd(mut a: u128, mut b: u128) -> u128 {
    while b != 0 {
        (a, b) = (b, a % b);
    }
    a.max(1)
}
impl std::ops::Add for Rational {
    type Output = Self;
    fn add(self, o: Self) -> Self {
        Self::new(self.n * o.d + o.n * self.d, self.d * o.d)
    }
}
impl std::ops::Sub for Rational {
    type Output = Self;
    fn sub(self, o: Self) -> Self {
        self + Self::new(-o.n, o.d)
    }
}
impl std::ops::Mul for Rational {
    type Output = Self;
    fn mul(self, o: Self) -> Self {
        Self::new(self.n * o.n, self.d * o.d)
    }
}
impl std::ops::Div for Rational {
    type Output = Self;
    fn div(self, o: Self) -> Self {
        Self::new(self.n * o.d, self.d * o.n)
    }
}

type Interval = (Rational, Rational);
fn scale(a: Rational, x: Interval) -> Interval {
    if a.n >= 0 {
        (a * x.0, a * x.1)
    } else {
        (a * x.1, a * x.0)
    }
}
fn add(x: Interval, y: Interval) -> Interval {
    (x.0 + y.0, x.1 + y.1)
}
fn square(x: Interval) -> Interval {
    let zero = Rational::integer(0);
    let low = if x.0 <= zero && zero <= x.1 {
        zero
    } else {
        min(x.0 * x.0, x.1 * x.1)
    };
    (low, max(x.0 * x.0, x.1 * x.1))
}
fn at(p: Rational, v: Rational, a: Rational, t: Rational) -> Rational {
    p + v * t + a * t * t / Rational::integer(2)
}

#[derive(Serialize)]
pub struct Case {
    name: String,
    inputs: Vec<String>,
    bounds: serde_json::Value,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub schema: &'static str,
    pub qualified: bool,
    pub arithmetic: &'static str,
    pub cases_checked: u64,
    pub cases: Vec<Case>,
    pub floating_point_qualified: bool,
    pub production_implemented: bool,
    pub performance_measured: bool,
}
pub fn bounds(p: Rational, v: Rational, a: Rational, lo: Rational, hi: Rational) -> [Interval; 5] {
    let naive = add(
        add((p, p), scale(v, (lo, hi))),
        scale(a / Rational::integer(2), square((lo, hi))),
    );
    let mid = (lo + hi) / Rational::integer(2);
    let h = (hi - lo) / Rational::integer(2);
    let centered = add(
        add(
            (at(p, v, a, mid), at(p, v, a, mid)),
            scale(v + a * mid, (Rational::new(-h.n, h.d), h)),
        ),
        scale(
            a / Rational::integer(2),
            square((Rational::new(-h.n, h.d), h)),
        ),
    );
    let controls = [
        at(p, v, a, lo),
        at(p, v, a, lo) + (v + a * lo) * (hi - lo) / Rational::integer(2),
        at(p, v, a, hi),
    ];
    let bernstein = (
        *controls.iter().min().unwrap(),
        *controls.iter().max().unwrap(),
    );
    let mut values = vec![at(p, v, a, lo), at(p, v, a, hi)];
    if a.n != 0 {
        let t = Rational::new(-v.n * a.d, v.d * a.n);
        if lo <= t && t <= hi {
            values.push(at(p, v, a, t));
        }
    }
    let exact = (*values.iter().min().unwrap(), *values.iter().max().unwrap());
    for interval in [naive, centered, bernstein] {
        assert!(
            interval.0 <= exact.0 && interval.1 >= exact.1,
            "interval {:?} exact {:?} p {:?} v {:?} a {:?} lo {:?} hi {:?}",
            interval,
            exact,
            p,
            v,
            a,
            lo,
            hi
        );
    }
    let intersection = (
        max(max(naive.0, centered.0), bernstein.0),
        min(min(naive.1, centered.1), bernstein.1),
    );
    assert!(intersection.0 <= exact.0 && intersection.1 >= exact.1);
    [naive, centered, bernstein, intersection, exact]
}
pub fn report() -> Report {
    let examples = [
        ("turn", 100, -20, 2, 0, 20),
        ("before-turn", 100, -20, 2, 0, 8),
        ("across-turn", 100, -20, 2, 9, 11),
        ("after-turn", 100, -20, 2, 12, 20),
        ("linear", 7, -3, 0, 0, 20),
        ("concave", -100, 20, -2, 0, 20),
    ];
    let cases=examples.into_iter().map(|(name,p,v,a,lo,hi)|{let q=[p,v,a,lo,hi].map(Rational::integer);let b=bounds(q[0],q[1],q[2],q[3],q[4]);let vals:Vec<Vec<String>>=b.iter().map(|x|vec![x.0.text(),x.1.text()]).collect();Case{name:name.into(),inputs:q.map(|x|x.text()).to_vec(),bounds:serde_json::json!({"naive":vals[0],"centered":vals[1],"bernstein":vals[2],"intersection":vals[3],"exact":vals[4]})}}).collect();
    let mut count = 0;
    for p in [-100, 0, 100] {
        for v in [-20, 0, 20] {
            for a in [-2, 0, 2] {
                for lo in 0..20 {
                    for hi in lo..21 {
                        bounds(
                            Rational::integer(p),
                            Rational::integer(v),
                            Rational::integer(a),
                            Rational::integer(lo),
                            Rational::integer(hi),
                        );
                        count += 1;
                    }
                }
            }
        }
    }
    Report {
        schema: "ksp-continuum-exact-curve-bound-toy/v1",
        qualified: true,
        arithmetic: "exact rational",
        cases_checked: count,
        cases,
        floating_point_qualified: false,
        production_implemented: false,
        performance_measured: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn checks_all_exact_cases() {
        let r = report();
        assert_eq!(r.cases_checked, 6210);
        assert!(r.qualified);
    }
}
