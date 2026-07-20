
from typing import Dict
from .trailer_load import optimize_trailer_load_plan
from ..exceptions import (
	TooManyPiecesException,
	NoPiecesException,
	PiecesTooLongForServiceException,
	OptimizationFailedServiceException,
	TrailerLoadingException,
	InvalidTrailerDimensionsException,
	InvalidPiecesException,
)
from ..optimizer_functions import PIECE_ARRANGEMENT_ROUTER, SHIPMENT_ARRANGEMENT_ROUTER
from ..standard_logistic_dims import STANDARD_TRAILER_DIMS
from .. import options
from copy import deepcopy
import numpy as np
import json
import logging
import threading

logger = logging.getLogger('ytl.trailer_load_api')

# Hard input guard. The upstream package defines TooManyPiecesException but never
# raises it; a pathological request with tens of thousands of pieces would otherwise
# run the O(n^2) bin-packer unbounded. 500 comfortably covers real LTL/partial loads.
MAX_PIECES = 500

# Default wall-clock guard so a single request can never pin a worker indefinitely.
# The optimizer's per-stage max_iter/timeout knobs bound most work already; this is a
# backstop for pathological geometry. Overridable per request via `max_seconds`.
DEFAULT_MAX_SECONDS = 25.0

STANDARD_TRAILER_DIM_MAP = {
	obj.get('code') : obj for obj in STANDARD_TRAILER_DIMS
}


def _count_pieces(shipment_list):
	'''Total expanded piece count (respecting num_pieces) for the input guard.'''
	total = 0
	for shipment in shipment_list:
		num = shipment.get('num_pieces', 1) if isinstance(shipment, dict) else 1
		try:
			num = int(num)
		except (TypeError, ValueError):
			num = 1
		total += max(num, 0)
	return total


def _run_with_timeout(fn, timeout_seconds):
	'''
	Run `fn` on a daemon thread, returning its result or raising TimeoutError if it
	overruns. The underlying compute is CPU-bound pure-Python and cannot be preempted,
	so the daemon thread is allowed to finish in the background (it will not block
	interpreter shutdown); the caller stops waiting and surfaces a timeout error.
	'''
	if timeout_seconds is None or timeout_seconds <= 0:
		return fn()
	box = {}

	def target():
		try:
			box['value'] = fn()
		except BaseException as exc:  # noqa: BLE001 - re-raised on the calling thread
			box['error'] = exc

	worker = threading.Thread(target=target, daemon=True)
	worker.start()
	worker.join(timeout_seconds)
	if worker.is_alive():
		raise TimeoutError(f'Optimization exceeded {timeout_seconds:g}s budget')
	if 'error' in box:
		raise box['error']
	return box['value']

class NumpyArrayEncoder(json.JSONEncoder):
	'''
	Add serialization rules for numpy int, float, and boolean objects
	'''
	def default(self, obj):
		# NumPy 2.0 removed np.int_/np.float_/np.bool_ aliases; use the concrete
		# scalar types (and the generic np.integer/np.floating bases) so the encoder
		# works on both numpy<2 and numpy>=2.
		if isinstance(obj, (np.integer, np.int64)):
			return int(obj)
		if isinstance(obj, (np.floating, np.float64)):
			return float(obj)
		if isinstance(obj, np.bool_):
			return bool(obj)
		return json.JSONEncoder.default(self, obj)

def parse_request_data(request_data : Dict):
	'''
	Parse Request Data for Trailer Load Optimization API Serivce

	Parameters
	-----------
	request_data : Dict
		Dictionary of the form 
	
	Returns
	-----------
	status_code : int
		Status code of 4** for invalid request data, 200 for valid request data
	parsed_request_data : Dict
		Parsed request data, {} if status_code is 4**
	errors : Dict
		Error dictionary, {} if status_code is 200
	'''
	parsed_request_data = {}
	
	# Parse trailer dims/equipment code
	if isinstance(request_data.get('trailer_dims'),dict):
		parsed_request_data.update({'trailer_dims' : request_data['trailer_dims']})
	elif request_data.get('equipment_code') in STANDARD_TRAILER_DIM_MAP:
		# map equipment_code to appropriate trailer_dims
		parsed_request_data.update({'trailer_dims' : STANDARD_TRAILER_DIM_MAP[request_data['equipment_code']]})
	elif isinstance(request_data.get('equipment_code'),str):
		# if equipment code is passed, but not an option return a 400 error
		return 400,{},{
			'error_code' : 'InvalidTrailerDimensionsException',
			'error_message' : 'Equipment code not recognized',
		}
	else:
		return 400,{},{
			'error_code' : 'InvalidTrailerDimensionsException',
			'error_message' : 'Invalid trailer dimensions or equipment type provided',
		}
	
	# Parse shipment list
	if isinstance(request_data.get('shipment_list'),list):
		parsed_request_data.update({'shipment_list' : request_data['shipment_list']})
	elif hasattr(request_data.get('shipment_list'),'__iter__'):
		parsed_request_data.update({'shipment_list' : list(request_data['shipment_list'])})
	else:
		return 400,{},{
			'error_code' : 'InvalidPiecesException',
			'error_message' : 'Invalid pieces provided',
		}
	if not all([isinstance(shipment,dict) for shipment in parsed_request_data['shipment_list']]):
		return 400,{},{
			'error_code' : 'InvalidPiecesException',
			'error_message' : 'Invalid pieces provided',
		}
	for shipment in parsed_request_data['shipment_list']:
		shipment.update({
			"dimension_unit_of_measure": options.DimUomInches,
			"weight_unit_of_measure": options.WeightUomPounds,
		})

	# Parse allow rotations parameter
	if request_data.get('allow_rotations') == False:
		parsed_request_data.update({'allow_rotations' : False})
	elif request_data.get('allow_rotations') == True or request_data.get('allow_rotations') is None:
		# if allow_rotation is passed as True or not provided, set true
		parsed_request_data.update({'allow_rotations' : True})
	else:
		# If allow rotations is not None and non-boolean, raise validation error
		return 400,{},{
			'error_code' : 'InvalidPiecesException',
			'error_message' : 'Invalid allow rotations parameter',
		}
	
	if request_data.get('overweight_shipment_threshold') is not None:
		try:
			parsed_request_data.update({'overweight_shipment_threshold' : float(request_data['overweight_shipment_threshold'])})
		except:
			# If allow rotations is not None and non-boolean, raise validation error
			return 400,{},{
				'error_code' : 'InvalidPiecesException',
				'error_message' : 'Invalid overweight shipment threshold parameter',
			}
	
	if request_data.get('piece_arrangement_algorithm') is not None:
		if request_data.get('piece_arrangement_algorithm') in PIECE_ARRANGEMENT_ROUTER:
			parsed_request_data.update({'piece_arrangement_algorithm' : request_data['piece_arrangement_algorithm']})
		else:
			return 400,{},{
				'error_code' : 'OptimizationFailedServiceException',
				'error_message' : 'Invalid piece arrangement parameter provided',
			}
	
	if request_data.get('shipment_optimization_ls') is not None:
		try:
			for params in request_data.get('shipment_optimization_ls'):
				assert isinstance(params,dict)
				assert params['algorithm'] in SHIPMENT_ARRANGEMENT_ROUTER
				assert isinstance(params.get('max_iter'),(int,type(None)))
				assert isinstance(params.get('timeout'),(int,float,type(None)))
			parsed_request_data.update({'shipment_optimization_ls' : request_data['shipment_optimization_ls']})
		except:
			return 400,{},{
				'error_code' : 'OptimizationFailedServiceException',
				'error_message' : 'Invalid shipment arrangement parameter provided',
			}

	return 200,parsed_request_data,{}


def optimize_trailer_load_plan_wrapper(request_data : Dict):
	'''
	Trailer Load Optimization Function Intended for Use with API

	Parameters
	------------
	request_data : Dict
		Request data for trailer loading optimization. In addition to the standard ytl
		fields it accepts two optional operational controls:
		  - `seed` (int): seeds numpy.random so the stochastic optimizer produces a
		    reproducible plan for an identical request.
		  - `max_seconds` (number): wall-clock budget for the optimization (default
		    DEFAULT_MAX_SECONDS). Exceeding it returns a 503 timeout error.

	Returns
	------------
	status_code : int
		Status code to be used for API response
	response_dict : Dict
		Response, trailer loading result when status_code is 200, error summary when status_code is not 200
	'''
	# Optional determinism: seed numpy's RNG before any stochastic stage runs so an
	# identical request yields an identical load_order.
	seed = request_data.get('seed') if isinstance(request_data, dict) else None
	if seed is not None:
		try:
			np.random.seed(int(seed))
		except (TypeError, ValueError):
			return 400,{
				'error_code' : 'InvalidRequestException',
				'error_message' : 'Invalid seed parameter; must be an integer',
			}

	# Optional per-request wall-clock guard.
	max_seconds = request_data.get('max_seconds') if isinstance(request_data, dict) else None
	if max_seconds is None:
		max_seconds = DEFAULT_MAX_SECONDS
	else:
		try:
			max_seconds = float(max_seconds)
		except (TypeError, ValueError):
			return 400,{
				'error_code' : 'InvalidRequestException',
				'error_message' : 'Invalid max_seconds parameter; must be numeric',
			}

	# Enforce the piece-count guard up front, before any expensive work.
	if isinstance(request_data, dict) and isinstance(request_data.get('shipment_list'), list):
		piece_count = _count_pieces(request_data['shipment_list'])
		if piece_count > MAX_PIECES:
			logger.warning(
				json.dumps({'event': 'too_many_pieces', 'piece_count': piece_count, 'max_pieces': MAX_PIECES})
			)
			return 400,{
				'error_code' : 'TooManyPiecesException',
				'error_message' : f'Too many pieces provided: {piece_count} exceeds maximum of {MAX_PIECES}',
			}

	# Parse request data
	try:
		request_status_code,parsed_request_data,errors = parse_request_data(request_data=deepcopy(request_data))
		if request_status_code != 200:
			logger.info(json.dumps({'event': 'request_rejected', 'status': request_status_code, 'error': errors.get('error_code')}))
			return request_status_code,errors
	except Exception as e:
		logger.warning(json.dumps({'event': 'parse_failed', 'error_type': type(e).__name__, 'detail': str(e)}))
		return 400,{
			'error_code' : 'InvalidRequestException',
			'error_message' : f'Invalid request: {e}',
		}

	# Do trailer load optimization
	try:
		trailer = _run_with_timeout(
			lambda: optimize_trailer_load_plan(**parsed_request_data),
			max_seconds,
		)
		if not trailer.arrangement_is_valid():
			logger.error(json.dumps({'event': 'invalid_arrangement'}))
			return 500,{
				'error_code' : 'TrailerLoadingException',
				'error_message' : 'Optimizer produced an invalid arrangement',
			}
		trailer_load_plan = trailer.get_summary()
		trailer_load_plan = json.loads(json.dumps(trailer_load_plan,cls=NumpyArrayEncoder))
		logger.info(json.dumps({
			'event': 'optimized',
			'num_pieces': trailer_load_plan.get('num_pieces'),
			'linear_feet': trailer_load_plan.get('linear_feet'),
			'seed': seed,
		}))
		return 200,trailer_load_plan
	except TimeoutError as e:
		logger.error(json.dumps({'event': 'timeout', 'detail': str(e)}))
		return 503,{
			'error_code' : 'OptimizationTimeoutException',
			'error_message' : str(e),
		}
	except TooManyPiecesException as e:
		return 400,{
			'error_code' : 'TooManyPiecesException',
			'error_message' : f'Too many pieces provided: {e}' if str(e) else 'Too many pieces provided',
		}
	except NoPiecesException:
		return 400,{
			'error_code' : 'NoPiecesException',
			'error_message' : 'No pieces provided',
		}
	except PiecesTooLongForServiceException:
		return 400,{
			'error_code' : 'PiecesTooLongForServiceException',
			'error_message' : 'At least one piece is too long for equipment',
		}
	except InvalidTrailerDimensionsException:
		return 400,{
			'error_code' : 'InvalidTrailerDimensionsException',
			'error_message' : 'Invalid trailer dimensions provided',
		}
	except InvalidPiecesException as e:
		# Surface the root cause (e.g. which dimension was non-positive) instead of a bare label.
		root = e.__cause__ or e
		detail = str(root)
		return 400,{
			'error_code' : 'InvalidPiecesException',
			'error_message' : f'One or more pieces have invalid dimensions: {detail}' if detail else 'One or more pieces have invalid dimensions',
		}
	except OptimizationFailedServiceException as e:
		root = e.__cause__ or e
		logger.error(json.dumps({'event': 'optimization_failed', 'error_type': type(root).__name__, 'detail': str(root)}))
		return 500,{
			'error_code' : 'OptimizationFailedServiceException',
			'error_message' : f'Optimization failed: {root}' if str(root) else 'Optimization failed',
		}
	except TrailerLoadingException as e:
		logger.error(json.dumps({'event': 'trailer_loading_error', 'detail': str(e)}))
		return 500,{
			'error_code' : 'TrailerLoadingException',
			'error_message' : f'Trailer loading error: {e}' if str(e) else 'Unknown error',
		}
	except Exception as e:
		logger.exception(json.dumps({'event': 'unhandled_error', 'error_type': type(e).__name__, 'detail': str(e)}))
		return 500,{
			'error_code' : 'Exception',
			'error_message' : f'Unexpected error: {e}' if str(e) else 'Unknown error',
		}
